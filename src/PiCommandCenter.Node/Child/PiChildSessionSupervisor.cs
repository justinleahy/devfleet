using System.Collections.Concurrent;
using System.Text.Json;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Node.Runtime;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Node.Repository;
using PiCommandCenter.Node.Security;

using PiCommandCenter.Node.RuntimeRouting;
using PiCommandCenter.Node.Verification;

namespace PiCommandCenter.Node.Child;

/// <summary>Stable child status values reported by <c>agent.status</c>.</summary>
public static class ChildAgentStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Blocked = "blocked";
}

/// <summary>
/// Child-agent supervisor for one node: implements the SPEC §13.3 orchestration tools that the
/// plain root handler gates (<c>agent.spawn</c>/<c>spawn_agents</c>, <c>agent.status</c>,
/// <c>agent.await</c>, <c>agent.cancel</c>), the Agent Mail tools, the reservation lifecycle
/// tools, and the reservation-authorized filesystem tools. Spawn enforces the root→child
/// hierarchy (only root sessions may spawn), the maximum running children per request, the
/// role allowlist and node-owned model routes, and unique agent names per request; every child gets a
/// parent link, <c>child.requested</c>/<c>child.started</c> events on the parent's durable
/// event stream, and a terminal <c>child.completed</c>/<c>child.failed</c>/
/// <c>child.cancelled</c> event. Child worker events are appended to the node spool directly.
/// No unrestricted shell or write tools exist: children mutate the repository only through
/// <see cref="ReservedFileOperations"/> after the reservation authority authorizes the lease
/// and fencing token.
/// </summary>
public sealed class PiChildSessionSupervisor
    : IPiOrchestrationRequestHandler, IRootSessionTerminalizer, IAsyncDisposable
{
    private readonly PiWorkerOptions _workerOptions;
    private readonly INodeRuntimeRoutingStore _routing;
    private readonly Lazy<IAgentRuntimeRegistry> _runtimes;
    private readonly Lazy<IRootSessionSupervisor> _rootSessions;
    private readonly Lazy<INodeAssignmentTerminalizationOrchestrator> _assignmentTerminalization;
    private readonly IPiOrchestrationRequestHandler _inner;
    private readonly INodeReservationGateway _reservations;
    private readonly INodeMailGateway _mail;
    private readonly IAgentIdentityRegistry _identities;
    private readonly INodeEventSpool _spool;
    private readonly INodeAssignmentCredentialSource _assignmentCredentials;
    private readonly IRequestVerificationCoordinator _verification;
    private readonly IRepositoryInspector _repository;
    private readonly IRuntimeCrashRecovery _crashRecovery;
    private readonly INodeCompletionGateway _completion;
    private readonly RequestWorkspaceTracker _workspace;
    private readonly ReservedFileOperations _fileOperations;
    private readonly TimeProvider _timeProvider;
    private readonly Quiescence.IRequestAdmissionGate _admission;
    /// <summary>Bound on the post-Begin drain before completion is reported as uncertain.</summary>
    private static readonly TimeSpan QuiescenceProofTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Per-child activity leases held until the child turns terminal.</summary>
    private readonly ConcurrentDictionary<string, Quiescence.NodeActivityLease> _childActivity =
        new(StringComparer.Ordinal);
    private readonly ILogger<PiChildSessionSupervisor> _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ChildAgent>>
        _childrenByRequest = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChildAgent> _childrenBySession = new(StringComparer.Ordinal);

    /// <summary>Requested-but-unaccepted lease handoffs, keyed by lease id.</summary>
    private readonly ConcurrentDictionary<Guid, (string FromSessionId, string ToSessionId)> _pendingHandoffs = new();

    public PiChildSessionSupervisor(
        IOptions<PiWorkerOptions> workerOptions,
        INodeRuntimeRoutingStore routing,
        IPiOrchestrationRequestHandler inner,
        INodeReservationGateway reservations,
        INodeMailGateway mail,
        IAgentIdentityRegistry identities,
        INodeEventSpool spool,
        INodeAssignmentCredentialSource assignmentCredentials,
        TimeProvider timeProvider,
        ILogger<PiChildSessionSupervisor> logger,
        Lazy<IAgentRuntimeRegistry> runtimes,
        Lazy<IRootSessionSupervisor> rootSessions,
        Lazy<INodeAssignmentTerminalizationOrchestrator> assignmentTerminalization,
        IRequestVerificationCoordinator verification,
        IRepositoryInspector repository,
        IRuntimeCrashRecovery crashRecovery,
        INodeCompletionGateway completion,
        RequestWorkspaceTracker workspace,
        Quiescence.IRequestAdmissionGate admission)
    {
        ArgumentNullException.ThrowIfNull(workerOptions);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(assignmentCredentials);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(rootSessions);
        ArgumentNullException.ThrowIfNull(assignmentTerminalization);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(crashRecovery);
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(admission);
        _workerOptions = workerOptions.Value;
        _routing = routing;
        _inner = inner;
        _reservations = reservations;
        _mail = mail;
        _identities = identities;
        _spool = spool;
        _assignmentCredentials = assignmentCredentials;
        _admission = admission;
        _fileOperations = new ReservedFileOperations(reservations, admission);
        _timeProvider = timeProvider;
        _logger = logger;
        _runtimes = runtimes;
        _rootSessions = rootSessions;
        _assignmentTerminalization = assignmentTerminalization;
        _verification = verification;
        _repository = repository;
        _crashRecovery = crashRecovery;
        _completion = completion;
        _workspace = workspace;
    }

    /// <summary>Non-terminal child count across all requests, for diagnostics.</summary>
    public int LiveChildCount => _childrenBySession.Values.Count(c => !c.IsTerminal);

    /// <summary>Session ids of non-terminal children hosted by this node.</summary>
    public IReadOnlyList<string> ActiveSessionIds
        => [.. _childrenBySession.Values.Where(child => !child.IsTerminal).Select(child => child.SessionId)];

    /// <summary>Non-terminal child session ids owned by one assignment request.</summary>
    public IReadOnlyList<string> ListLiveChildSessionIds(Guid requestId)
    {
        foreach (var pair in _childrenByRequest)
        {
            if (!Guid.TryParse(pair.Key, out var id) || id != requestId)
            {
                continue;
            }

            return
            [
                .. pair.Value.Values
                    .Where(child => !child.IsTerminal)
                    .Select(child => child.SessionId),
            ];
        }

        return [];
    }


    public async Task<PiToolResponse> HandleAsync(
        PiOrchestrationContext context,
        string requestType,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        Quiescence.NodeActivityLease? activity = null;
        try
        {
            if (requestType is not (
                    "request.complete"
                    or "submit_completion"
                    or "verification.request"
                    or "request_verification"
                    or "verification.intermediate.request")
                && Guid.TryParse(context.RequestId, out var requestId))
            {
                activity = _admission.TryEnterOperation(
                    requestId,
                    $"pi-tool:{requestType}");
                if (activity is null)
                {
                    return AdmissionClosed();
                }
            }

            using var admittedActivity = activity;
            return requestType switch
            {
                "agent.spawn" or "spawn_agent" => await SpawnAsync(context, payload, single: true, cancellationToken),
                "spawn_agents" => await SpawnAsync(context, payload, single: false, cancellationToken),
                "agent.status" or "get_agent_status" => Status(context, payload),
                "agent.await" or "await_agent" => await AwaitAsync(context, payload, cancellationToken),
                "agent.cancel" or "cancel_agent" => await CancelAsync(context, payload, cancellationToken),
                "agent.message.send" or "send_agent_message" => await SendMessageAsync(context, payload, cancellationToken),
                "agent.inbox.read" or "read_agent_inbox" => await ReadInboxAsync(context, payload, cancellationToken),
                "agent.message.acknowledge" or "acknowledge_message" => await AcknowledgeAsync(context, payload, cancellationToken),
                "reservation.acquire" or "reserve_files" => await AcquireAsync(context, payload, cancellationToken),
                "reservation.expand" or "expand_reservation" => await ExpandAsync(context, payload, cancellationToken),
                "reservation.release" or "release_reservation" => await ReleaseAsync(context, payload, cancellationToken),
                "reservation.handoff.request" or "request_reservation_handoff" => await RequestHandoffAsync(context, payload, cancellationToken),
                "reservation.handoff.accept" or "accept_reservation_handoff" => await AcceptHandoffAsync(context, payload, cancellationToken),
                "project.diff.inspect" or "inspect_project_diff" => await InspectDiffAsync(context, cancellationToken),
                "verification.request" or "request_verification" => await RequestVerificationAsync(context, payload, cancellationToken),
                "verification.intermediate.request" => await RequestIntermediateVerificationAsync(context, payload, cancellationToken),
                "request.complete" or "submit_completion" => await CompleteRequestAsync(context, payload, cancellationToken),
                "child.result.submit" or "submit_child_result" => PiToolResponse.Success(
                    payload is { } result ? JsonSerializer.Deserialize<object>(result.GetRawText()) : new { }),
                "reserved_read" => await ReservedReadAsync(context, payload, cancellationToken),
                "workspace.read" or "read" => WorkspaceQuery(
                    context.RepositoryRoot,
                    WorkspaceReadOperations.Read(context.RepositoryRoot ?? "", payload.GetStringProperty("path"))),
                "workspace.grep" or "grep" => WorkspaceQuery(
                    context.RepositoryRoot,
                    WorkspaceReadOperations.Grep(
                        context.RepositoryRoot ?? "",
                        payload.GetStringProperty("path"),
                        payload.GetStringProperty("pattern"))),
                "workspace.find" or "find" => WorkspaceQuery(
                    context.RepositoryRoot,
                    WorkspaceReadOperations.Find(
                        context.RepositoryRoot ?? "",
                        payload.GetStringProperty("path"),
                        payload.GetStringProperty("pattern") ?? payload.GetStringProperty("glob"))),
                "workspace.ls" or "ls" => WorkspaceQuery(
                    context.RepositoryRoot,
                    WorkspaceReadOperations.List(context.RepositoryRoot ?? "", payload.GetStringProperty("path"))),
                "reserved_write" => await RunSinglePathMutationAsync(
                    context, payload,
                    (ops, requestId, root, lease, sessionId, path, cancellationToken) => ops.WriteTextAsync(
                        requestId, root, lease, sessionId, path, payload.GetStringProperty("content") ?? string.Empty, cancellationToken),
                    cancellationToken),
                "reserved_edit" => await RunSinglePathMutationAsync(
                    context, payload,
                    (ops, requestId, root, lease, sessionId, path, cancellationToken) => ops.EditTextAsync(
                        requestId, root, lease, sessionId, path,
                        payload.GetStringProperty("oldText") ?? string.Empty,
                        payload.GetStringProperty("newText") ?? string.Empty,
                        cancellationToken),
                    cancellationToken),
                "reserved_delete" => await RunSinglePathMutationAsync(
                    context, payload,
                    (ops, requestId, root, lease, sessionId, path, cancellationToken) => ops.DeleteAsync(requestId, root, lease, sessionId, path, cancellationToken),
                    cancellationToken),
                "reserved_move" => await ReservedMoveAsync(context, payload, cancellationToken),
                _ => await _inner.HandleAsync(context, requestType, payload, cancellationToken),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Child supervisor tool '{RequestType}' failed on session {SessionId}.",
                requestType, context.SessionId);
            return PiToolResponse.Failure("child_supervisor_error", ex.Message);
        }
    }

    private static PiToolResponse AdmissionClosed()
        => PiToolResponse.Failure(
            "admission_closed",
            "The request is terminalizing; no new work is admitted.");

    private async Task<PiToolResponse> InspectDiffAsync(
        PiOrchestrationContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(context.RepositoryRoot)
            || !Guid.TryParse(context.RequestId, out var requestId)
            || !Guid.TryParse(context.ProjectId, out var projectId))
        {
            return PiToolResponse.Failure("repository_root_unknown", "The session has no repository bound.");
        }

        if (!_workspace.TryGetBaseline(requestId, out var baseline))
        {
            return PiToolResponse.Failure("baseline_missing", "No repository baseline was captured for this request.");
        }

        var leases = await _reservations.ListAsync(projectId, includeReleased: false, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await _repository.DetectExternalChangesAsync(
                context.RepositoryRoot, baseline.BaseCommit, leases, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ExternalRepositoryModificationException ex)
        {
            await context.EmitAsync(
                "repository.external_change_detected",
                new Dictionary<string, object?> { ["paths"] = ex.Paths },
                cancellationToken).ConfigureAwait(false);
            return PiToolResponse.Failure(
                "external_repository_modification",
                "BLOCKED — Unattributed external repository modification");
        }

        var inspection = await _repository.InspectDiffAsync(
            context.RepositoryRoot, baseline.BaseCommit, leases, cancellationToken)
            .ConfigureAwait(false);
        await context.EmitAsync(
            "repository.changed",
            new Dictionary<string, object?>
            {
                ["paths"] = inspection.ChangedFiles.Select(f => f.Path).ToArray(),
                ["branch"] = inspection.Branch,
                ["baseCommit"] = inspection.BaseCommit,
            },
            cancellationToken).ConfigureAwait(false);
        return PiToolResponse.Success(new Dictionary<string, object?>
        {
            ["branch"] = inspection.Branch,
            ["baseCommit"] = inspection.BaseCommit,
            ["changedFiles"] = inspection.ChangedFiles,
            ["unattributedPaths"] = inspection.UnattributedPaths,
        });
    }

    private Task<PiToolResponse> RequestVerificationAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        ReportIgnoredVerificationSelectors(context, payload);
        return RunVerificationAsync(
            context,
            intermediate: context.ParentSessionId is not null,
            cancellationToken);
    }

    private Task<PiToolResponse> RequestIntermediateVerificationAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        if (context.ParentSessionId is null)
        {
            return Task.FromResult(PiToolResponse.Failure(
                "intermediate_not_from_child",
                "Only a child session may request intermediate verification."));
        }

        ReportIgnoredVerificationSelectors(context, payload);
        return RunVerificationAsync(context, intermediate: true, cancellationToken);
    }

    private async Task<PiToolResponse> RunVerificationAsync(
        PiOrchestrationContext context,
        bool intermediate,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.RequestId, out var requestId)
            || !Guid.TryParse(context.ProjectId, out var projectId))
        {
            return PiToolResponse.Failure(
                "request_identity_unknown",
                "The session is not bound to a request.");
        }

        if (intermediate)
        {
            var leases = await _reservations
                .ListAsync(projectId, includeReleased: false, cancellationToken)
                .ConfigureAwait(false);
            if (leases.Any(lease =>
                    string.Equals(lease.OwnerSessionId, context.SessionId, StringComparison.Ordinal)
                    && !string.Equals(lease.State, "Released", StringComparison.OrdinalIgnoreCase)
                    && lease.Scopes.Any(IsSourceScope)))
            {
                return PiToolResponse.Failure(
                    "source_reservation_active",
                    "Release this child's file or directory reservations before requesting project checks.");
            }
        }

        var verificationContext = await CreateVerificationContextAsync(
            context, projectId, requestId, cancellationToken).ConfigureAwait(false);
        var decision = intermediate
            ? await _verification.VerifyIntermediateAsync(verificationContext, cancellationToken)
                .ConfigureAwait(false)
            : await _verification.VerifyFinalAsync(verificationContext, cancellationToken)
                .ConfigureAwait(false);
        return ToVerificationResponse(decision);
    }

    private async Task<PiToolResponse> CompleteRequestAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.RequestId, out var requestId)
            || !Guid.TryParse(context.ProjectId, out var projectId))
        {
            return PiToolResponse.Failure(
                "request_identity_unknown",
                "The session is not bound to a request.");
        }

        using var terminalization = _admission.TryEnterTerminalization(
            requestId,
            $"terminalize:{context.SessionId}");
        if (terminalization is null)
        {
            return AdmissionClosed();
        }

        var summary = payload.GetStringProperty("summaryMarkdown")
            ?? payload.GetStringProperty("summary")
            ?? string.Empty;
        var findings = ParseFindings(payload);
        IReadOnlyList<string> changedFiles = payload.GetStringListProperty("changedFiles") ?? [];

        if (string.IsNullOrEmpty(context.RepositoryRoot) is false
            && _workspace.TryGetBaseline(requestId, out var baseline))
        {
            var leases = await _reservations.ListAsync(projectId, includeReleased: true, cancellationToken)
                .ConfigureAwait(false);
            var inspection = await _repository.InspectDiffAsync(
                context.RepositoryRoot, baseline.BaseCommit, leases, cancellationToken)
                .ConfigureAwait(false);
            if (changedFiles.Count == 0)
            {
                changedFiles = [.. inspection.ChangedFiles.Select(file => file.Path)];
            }
        }

        string? requestBranch = null;
        string? checkpointCommitId = null;
        if (context.CreateCheckpointAsync is not null)
        {
            if (changedFiles.Count == 0)
            {
                return PiToolResponse.Failure(
                    "checkpoint_requires_paths",
                    "Completion cannot create the configured request checkpoint without changed files.");
            }

            var checkpoint = await context.CreateCheckpointAsync(
                new PiCheckpointRequest(
                    payload.GetStringProperty("branchName") ?? string.Empty,
                    payload.GetStringProperty("message") ?? $"Complete request {requestId}",
                    changedFiles),
                cancellationToken).ConfigureAwait(false);
            if (!checkpoint.Ok)
            {
                return PiToolResponse.Failure(
                    checkpoint.ErrorCode ?? "checkpoint_failed",
                    checkpoint.ErrorMessage ?? "Checkpoint commit failed.");
            }

            requestBranch = checkpoint.BranchName;
            checkpointCommitId = checkpoint.CommitId;
            await context.EmitAsync(
                "repository.checkpoint_created",
                new Dictionary<string, object?>
                {
                    ["branchName"] = requestBranch,
                    ["commitId"] = checkpointCommitId,
                    ["paths"] = changedFiles,
                },
                cancellationToken).ConfigureAwait(false);
        }

        var verificationContext = await CreateVerificationContextAsync(
            context, projectId, requestId, cancellationToken).ConfigureAwait(false);
        var verificationDecision = await _verification
            .VerifyFinalAsync(verificationContext, cancellationToken)
            .ConfigureAwait(false);
        if (!verificationDecision.IsGreen)
        {
            return ToVerificationResponse(verificationDecision);
        }

        var sealedAdmission = false;
        try
        {
            if (!_admission.TrySealAdmission(requestId))
            {
                return AdmissionClosed();
            }

            sealedAdmission = true;
            if (!await _admission.WaitUntilDrainedAsync(
                    requestId, QuiescenceProofTimeout, cancellationToken).ConfigureAwait(false))
            {
                return PiToolResponse.Failure(
                    "quiescence_uncertain",
                    "Preexisting request activity did not drain before fingerprint capture.");
            }

            string currentFingerprint;
            try
            {
                currentFingerprint = await _verification
                    .CaptureFingerprintAsync(verificationContext, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (VerificationRejectedException ex)
            {
                return PiToolResponse.Failure(ex.Code, ex.Message);
            }

            if (!string.Equals(
                    currentFingerprint,
                    verificationDecision.Fingerprint,
                    StringComparison.Ordinal)
                || !string.Equals(
                    verificationDecision.PolicyRevision,
                    verificationContext.Policy?.Revision,
                    StringComparison.Ordinal))
            {
                return PiToolResponse.Failure(
                    "verification_stale",
                    "The repository changed after verification; run final verification again.");
            }

        var evidence = new CompletionEvidence(
            summary,
            changedFiles,
            findings,
            verificationDecision.Summary,
            requestBranch,
            checkpointCommitId,
            verificationDecision.Fingerprint,
            verificationDecision.PolicyRevision);
        var decision = await BeginTerminalizationAsync(
            projectId, requestId, context.SessionId,
            TerminalizationIntent.Complete, evidence, reason: null, cancellationToken)
            .ConfigureAwait(false);

        if (!decision.Accepted)
        {
            await context.EmitAsync(
                "request.completion_rejected",
                new Dictionary<string, object?>
                {
                    ["missingRequirements"] = decision.MissingRequirements,
                },
                cancellationToken).ConfigureAwait(false);
            return PiToolResponse.Success(new Dictionary<string, object?>
            {
                ["accepted"] = false,
                ["missingRequirements"] = decision.MissingRequirements,
            });
        }
        _admission.CloseAdmission(requestId);
        sealedAdmission = false;
        terminalization.Dispose();
        var outcome = await _admission.ProveQuiescenceAsync(
            requestId,
            BuildQuiescenceObservation(
                projectId, requestId, context.SessionId, context.RepositoryRoot),
            QuiescenceProofTimeout,
            cancellationToken).ConfigureAwait(false);

        if (outcome is not Quiescence.QuiescenceOutcome.Proven proven)
        {
            var uncertain = (Quiescence.QuiescenceOutcome.Uncertain)outcome;
            _logger.LogWarning(
                "Quiescence for request {RequestId} could not be proven ({Reason}); "
                + "confirmation withheld. Observed: {@Observed}",
                requestId, uncertain.Reason, uncertain.Observed);
            return PiToolResponse.Failure(
                "quiescence_uncertain",
                $"The assignment could not be proven quiescent ({uncertain.Reason}); "
                + "completion was not confirmed.");
        }

        var proof = ToApplicationProof(proven.Proof);
        decision = await _completion.ConfirmTerminalizationAsync(
            projectId, requestId, context.SessionId,
            TerminalizationIntent.Complete, evidence, reason: null, proof, cancellationToken)
            .ConfigureAwait(false);

        if (!decision.Accepted)
        {
            await context.EmitAsync(
                "request.completion_rejected",
                new Dictionary<string, object?>
                {
                    ["missingRequirements"] = decision.MissingRequirements,
                },
                cancellationToken).ConfigureAwait(false);
            return PiToolResponse.Success(new Dictionary<string, object?>
            {
                ["accepted"] = false,
                ["missingRequirements"] = decision.MissingRequirements,
            });
        }

        _admission.CommitTerminalization(requestId);
        await context.EmitAsync(
            "request.completed",
            new Dictionary<string, object?> { ["summaryMarkdown"] = summary },
            cancellationToken).ConfigureAwait(false);
        return PiToolResponse.Success(new Dictionary<string, object?>
        {
            ["accepted"] = true,
            ["missingRequirements"] = Array.Empty<string>(),
            ["result"] = decision.Result,
        });
        }
        finally
        {
            if (sealedAdmission)
            {
                _admission.UnsealAdmission(requestId);
            }
        }
    }

    public Task<RootTerminalizationOutcome> CancelAsync(
        ExecutionAssignmentMessage assignment,
        string rootSessionId,
        string reason,
        CancellationToken cancellationToken)
        => TerminalizeRootAsync(
            assignment,
            rootSessionId,
            reason,
            TerminalizationIntent.Cancel,
            cancellationToken);
    public Task<RootTerminalizationOutcome> CancelBeforeRootAsync(
        ExecutionAssignmentMessage assignment,
        string reason,
        CancellationToken cancellationToken)
        => TerminalizeRootAsync(
            assignment,
            rootSessionId: null,
            reason,
            TerminalizationIntent.Cancel,
            cancellationToken);


    public Task<RootTerminalizationOutcome> FailAsync(
        ExecutionAssignmentMessage assignment,
        string rootSessionId,
        string reason,
        CancellationToken cancellationToken)
        => TerminalizeRootAsync(
            assignment,
            rootSessionId,
            reason,
            TerminalizationIntent.Fail,
            cancellationToken);

    private async Task<RootTerminalizationOutcome> TerminalizeRootAsync(
        ExecutionAssignmentMessage assignment,
        string? rootSessionId,
        string reason,
        TerminalizationIntent intent,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        using var terminalization = _admission.TryEnterTerminalization(
            assignment.RequestId,
            $"terminalize:{rootSessionId ?? "pre-root"}");
        if (terminalization is null)
        {
            return RootTerminalizationOutcome.Rejected;
        }

        var decision = await BeginTerminalizationAsync(
            assignment.ProjectId,
            assignment.RequestId,
            rootSessionId,
            intent,
            evidence: null,
            reason,
            cancellationToken).ConfigureAwait(false);
        if (!decision.Accepted)
        {
            return RootTerminalizationOutcome.Rejected;
        }

        _admission.CloseAdmission(assignment.RequestId);
        terminalization.Dispose();

        var stopTasks = new List<Task>();
        foreach (var child in _childrenBySession.Values)
        {
            if (!child.IsTerminal
                && Guid.TryParse(child.RequestId, out var childRequestId)
                && childRequestId == assignment.RequestId)
            {
                stopTasks.Add(CancelSessionAsync(child.SessionId, reason));
            }
        }

        if (intent == TerminalizationIntent.Cancel && rootSessionId is not null)
        {
            stopTasks.Add(_rootSessions.Value.CancelSessionAsync(rootSessionId, reason));
        }

        await Task.WhenAll(stopTasks).ConfigureAwait(false);

        var outcome = await _admission.ProveQuiescenceAsync(
            assignment.RequestId,
            BuildQuiescenceObservation(
                assignment.ProjectId,
                assignment.RequestId,
                rootSessionId,
                assignment.CanonicalRepositoryPathSnapshot),
            QuiescenceProofTimeout,
            cancellationToken).ConfigureAwait(false);
        if (outcome is not Quiescence.QuiescenceOutcome.Proven proven)
        {
            var uncertain = (Quiescence.QuiescenceOutcome.Uncertain)outcome;
            _logger.LogWarning(
                "{Intent} quiescence for request {RequestId} could not be proven ({Reason}); "
                + "confirmation withheld. Observed: {@Observed}",
                intent,
                assignment.RequestId,
                uncertain.Reason,
                uncertain.Observed);
            return RootTerminalizationOutcome.Uncertain;
        }

        decision = await _completion.ConfirmTerminalizationAsync(
            assignment.ProjectId,
            assignment.RequestId,
            rootSessionId,
            intent,
            evidence: null,
            reason,
            ToApplicationProof(proven.Proof),
            cancellationToken).ConfigureAwait(false);
        if (!decision.Accepted)
        {
            return RootTerminalizationOutcome.Rejected;
        }

        _admission.CommitTerminalization(assignment.RequestId);
        return RootTerminalizationOutcome.Accepted;
    }

    private Task<CompletionGateDecision> BeginTerminalizationAsync(
        Guid projectId,
        Guid requestId,
        string? rootSessionId,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        CancellationToken cancellationToken)
        => _assignmentTerminalization.Value.BeginTerminalizationAsync(
            requestId,
            intent,
            token => _completion.BeginTerminalizationAsync(
                projectId,
                requestId,
                rootSessionId,
                intent,
                evidence,
                reason,
                token),
            cancellationToken);

    /// <summary>
    /// External observations the admission gate cannot perform itself: request-owned active
    /// reservations, request-scoped pending spool events, and repository inspection.
    /// </summary>
    private Quiescence.QuiescenceObservation BuildQuiescenceObservation(
        Guid projectId,
        Guid requestId,
        string? rootSessionId,
        string? repositoryRoot)
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        if (rootSessionId is not null)
        {
            owners.Add(rootSessionId);
        }
        foreach (var child in _childrenBySession.Values)
        {
            if (Guid.TryParse(child.RequestId, out var childRequestId)
                && childRequestId == requestId)
            {
                owners.Add(child.SessionId);
            }
        }

        return new Quiescence.QuiescenceObservation(
            async ct =>
            {
                var leases = await _reservations.ListAsync(projectId, includeReleased: false, ct)
                    .ConfigureAwait(false);
                return leases.Count(lease =>
                    !string.Equals(lease.State, "Released", StringComparison.Ordinal)
                    && owners.Contains(lease.OwnerSessionId));
            },
            ct => _spool.CountPendingForRequestAsync(requestId, ct),
            async ct =>
            {
                if (string.IsNullOrEmpty(repositoryRoot))
                {
                    return false;
                }

                if (!_workspace.TryGetBaseline(requestId, out var baseline))
                {
                    _ = await _repository.CaptureBaselineAsync(
                        repositoryRoot,
                        requireCleanStart: false,
                        allowUntrackedFiles: true,
                        ct).ConfigureAwait(false);
                    return true;
                }

                var leases = await _reservations.ListAsync(projectId, includeReleased: false, ct)
                    .ConfigureAwait(false);
                await _repository.DetectExternalChangesAsync(
                    repositoryRoot, baseline.BaseCommit, leases, ct).ConfigureAwait(false);
                return true;
            });
    }

    private static AssignmentQuiescenceProof ToApplicationProof(
        Quiescence.AssignmentQuiescenceProof proof)
        => new(
            proof.AdmissionClosed,
            proof.ActiveChildren,
            proof.ActiveOperations,
            proof.ActiveProcesses,
            proof.PendingEvents,
            proof.ActiveReservations,
            proof.RepositoryInspected,
            proof.ObservedAt);

    private async Task<RequestVerificationContext> CreateVerificationContextAsync(
        PiOrchestrationContext context,
        Guid projectId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        _workspace.TryGetBaseline(requestId, out var baseline);
        var persisted = await _completion.ListVerificationRunsAsync(
            context.SessionId,
            projectId,
            requestId,
            cancellationToken).ConfigureAwait(false);
        return new RequestVerificationContext(
            projectId,
            requestId,
            context.WorkspaceBindingId,
            context.BindingValidationRevision,
            context.SessionId,
            context.RepositoryRoot ?? string.Empty,
            baseline?.BaseCommit ?? string.Empty,
            baseline?.Branch ?? string.Empty,
            CreateVerificationPolicy(context),
            persisted.Count > 0 ? persisted : context.ExistingVerificationRuns ?? [],
            context.EmitAsync,
            (run, token) => _completion.RecordVerificationRunAsync(context.SessionId, run, token));
    }

    private static bool IsSourceScope(ReservationScopeSpec scope) =>
        !string.Equals(scope.Kind, "resource", StringComparison.OrdinalIgnoreCase);

    private static string TruncateLeaseError(string message) =>
        message.Length <= 256 ? message : message[..256];

    private static RequestVerificationPolicy? CreateVerificationPolicy(
        PiOrchestrationContext context)
    {
        var hasSnapshot = context.VerificationPolicyRevision is not null
            || context.BaselineVersion is not null
            || context.TrustedVerificationProfileId is not null
            || context.TrustedVerificationProfileRevision is not null
            || context.MandatoryVerificationCommandIds is not null;
        if (!hasSnapshot)
        {
            return null;
        }

        return new RequestVerificationPolicy(
            context.VerificationPolicyRevision ?? string.Empty,
            context.BaselineVersion ?? string.Empty,
            context.TrustedVerificationProfileId,
            context.TrustedVerificationProfileRevision,
            context.MandatoryVerificationCommandIds ?? []);
    }

    private void ReportIgnoredVerificationSelectors(
        PiOrchestrationContext context,
        JsonElement? payload)
    {
        if (payload is not JsonElement element
            || element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("profileId", out _)
                && !element.TryGetProperty("commandId", out _))
        {
            return;
        }

        _logger.LogWarning(
            "Legacy verification profileId/commandId selectors from session {SessionId} "
            + "were ignored; verification policy is node-owned.",
            context.SessionId);
    }

    private static PiToolResponse ToVerificationResponse(
        RequestVerificationDecision decision)
    {
        if (decision.IsGreen)
        {
            return PiToolResponse.Success(new Dictionary<string, object?>
            {
                ["decision"] = decision.Kind.ToString(),
                ["fingerprint"] = decision.Fingerprint,
                ["policyRevision"] = decision.PolicyRevision,
                ["summary"] = decision.Summary,
            });
        }

        var errorCode = decision.ErrorCode ?? (decision.Kind switch
        {
            RequestVerificationDecisionKind.Failed => "verification_failed",
            RequestVerificationDecisionKind.Cancelled => "verification_cancelled",
            _ => "verification_rejected",
        });
        return PiToolResponse.Failure(errorCode, decision.Summary);
    }

    private static IReadOnlyList<ReviewFinding> ParseFindings(JsonElement? payload)
    {
        if (payload is not JsonElement root
            || root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("reviewFindings", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var findings = new List<ReviewFinding>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            findings.Add(new ReviewFinding(
                item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() ?? string.Empty
                    : string.Empty,
                item.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString() ?? string.Empty
                    : string.Empty,
                item.TryGetProperty("blocking", out var b) && b.ValueKind == JsonValueKind.True,
                item.TryGetProperty("resolved", out var r) && r.ValueKind == JsonValueKind.True,
                item.TryGetProperty("userOverridden", out var o) && o.ValueKind == JsonValueKind.True));
        }

        return findings;
    }


    // ---- Spawn ----

    private async Task<PiToolResponse> SpawnAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        bool single,
        CancellationToken cancellationToken)
    {
        if (context.ParentSessionId is not null)
        {
            return PiToolResponse.Failure(
                "spawn_not_from_root", "Only a root session may spawn child agents.");
        }

        if (string.IsNullOrEmpty(context.RepositoryRoot))
        {
            return PiToolResponse.Failure(
                "repository_root_unknown", "The session has no repository root; cannot spawn a child.");
        }

        var specs = ParseSpawnSpecs(payload, single, out var parseError);
        if (parseError is not null)
        {
            return PiToolResponse.Failure(parseError.Code, parseError.Message);
        }

        if (Guid.TryParse(context.RequestId, out var spawnRequestId)
            && _admission.IsAdmissionClosed(spawnRequestId))
        {
            return PiToolResponse.Failure(
                "admission_closed", "The request is terminalizing; no new child work is admitted.");
        }

        var children = _childrenByRequest.GetOrAdd(context.RequestId, _ => new(StringComparer.Ordinal));
        var runningCount = children.Values.Count(c => !c.IsTerminal);
        if (runningCount + specs.Count > _workerOptions.MaxChildAgentsPerRequest)
        {
            return PiToolResponse.Failure(
                "max_child_agents_exceeded",
                $"Spawning {specs.Count} children would exceed the maximum of "
                + $"{_workerOptions.MaxChildAgentsPerRequest} running children per request.");
        }

        var results = new List<object?>(specs.Count);
        foreach (var spec in specs)
        {
            results.Add(await SpawnOneAsync(context, children, spec, cancellationToken).ConfigureAwait(false));
        }

        return PiToolResponse.Success(single ? results[0] : results);
    }

    private async Task<object?> SpawnOneAsync(
        PiOrchestrationContext context,
        ConcurrentDictionary<string, ChildAgent> children,
        SpawnSpec spec,
        CancellationToken cancellationToken)
    {
        var routing = _routing.Current;
        if (!routing.AllowedRoles.Contains(spec.Role, StringComparer.Ordinal))
        {
            return SpawnFailure("role_not_allowed", $"Role '{spec.Role}' is not in the role allowlist.");
        }

        var candidates = routing.RoleRoutes.FirstOrDefault(
            route => string.Equals(route.Role, spec.Role, StringComparison.Ordinal))?.Candidates;
        if (candidates is null || candidates.Count == 0)
        {
            return SpawnFailure(
                "role_route_not_configured",
                $"Role '{spec.Role}' has no configured runtime/model route.");
        }

        var failures = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await SpawnCandidateAsync(
                context,
                children,
                spec,
                candidate,
                cancellationToken).ConfigureAwait(false);
            if (result is not Dictionary<string, object?> envelope
                || !envelope.TryGetValue("error", out var errorValue)
                || errorValue is not Dictionary<string, object?> error)
            {
                return result;
            }

            var code = error.GetValueOrDefault("code") as string;
            var message = error.GetValueOrDefault("message") as string ?? "candidate failed";
            if (code is not ("child_start_failed" or "reservation_required" or "runtime_read_only"))
            {
                return result;
            }

            failures.Add($"{candidate.Model}: {message}");
        }

        return SpawnFailure(
            "runtime_route_exhausted",
            $"Every configured candidate for role '{spec.Role}' failed in order: "
            + string.Join("; ", failures));
    }

    private async Task<object?> SpawnCandidateAsync(
        PiOrchestrationContext context,
        ConcurrentDictionary<string, ChildAgent> children,
        SpawnSpec spec,
        RuntimeRouteCandidateMessage candidate,
        CancellationToken cancellationToken)
    {
        AgentModelSelector selector;
        IAgentRuntimeAdapter adapter;
        try
        {
            selector = AgentModelSelector.Parse(candidate.Model);
            adapter = _runtimes.Value.Resolve(selector);
        }
        catch (Exception ex)
        {
            return SpawnFailure("model_not_allowed", ex.Message);
        }

        var model = selector.Value;
        var requestedScopes = spec.RequestedWriteScopes
            .Select(s => new ReservationScopeSpec(s.Kind, s.Path))
            .ToArray();

        // Antigravity is OS-sandboxed read-only and never receives a lease; a write request
        // cannot be honored by this candidate, so fail it before allocating anything.
        if (requestedScopes.Length > 0 && adapter.RuntimeKind == AgentRuntimeKinds.Antigravity)
        {
            return SpawnFailure(
                "runtime_read_only",
                $"'{model}' is read-only and cannot take requested write scopes.");
        }

        var sessionId = $"pi-child-{context.RequestId:N}-{Guid.NewGuid():N}";
        var identity = await _identities.AllocateAsync(
            new AllocateAgentIdentityCommand(
                new ProjectId(Guid.Parse(context.ProjectId)),
                sessionId,
                spec.AgentName,
                spec.Role,
                adapter.RuntimeKind),
            cancellationToken).ConfigureAwait(false);
        var agentName = identity.AgentName;

        // The project registry owns collision-safe names; every runtime and event uses it.
        var child = new ChildAgent(
            sessionId,
            agentName,
            spec.Role,
            model,
            adapter.RuntimeKind,
            context.SessionId,
            context.RequestId,
            context.ProjectId,
            context.NodeId,
            context.RepositoryRoot!,
            _timeProvider.GetUtcNow());
        if (!children.TryAdd(agentName, child))
        {
            await _identities.ReleaseAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            return SpawnFailure(
                "duplicate_agent_name",
                $"An agent named '{agentName}' already exists for this request.");
        }

        var childActivity = _admission.TryAdmitChild(Guid.Parse(context.RequestId), sessionId);
        if (childActivity is null)
        {
            children.TryRemove(agentName, out _);
            _childrenBySession.TryRemove(sessionId, out _);
            await _identities.ReleaseAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            return SpawnFailure(
                "admission_closed", "The request is terminalizing; no new child work is admitted.");
        }

        _childActivity[sessionId] = childActivity;

        if (!_childrenBySession.TryAdd(sessionId, child))
        {
            children.TryRemove(agentName, out _);
            await _identities.ReleaseAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            UntrackChild(sessionId);
            return SpawnFailure("duplicate_agent_name", "Child session id collision; retry the spawn.");
        }

        await EmitOnParentAsync(
            context,
            "child.requested",
            new Dictionary<string, object?>
            {
                ["childSessionId"] = sessionId,
                ["agentName"] = agentName,
                ["role"] = spec.Role,
                ["model"] = model,
                ["parentSessionId"] = context.SessionId,
                ["requestedWriteScopes"] = spec.RequestedWriteScopes,
            },
            cancellationToken).ConfigureAwait(false);

        // Reserve the requested write scopes for the child before it starts. Pi children fall
        // back to read-only tools on denial (the error is surfaced to the caller); Claude's
        // write permissions come only from a granted lease, so a denied Claude write fails.
        ReservationOperationResult? leaseResult = null;
        if (requestedScopes.Length > 0)
        {
            leaseResult = await _reservations.AcquireAsync(
                Guid.Parse(context.ProjectId),
                Guid.Parse(context.RequestId),
                sessionId,
                requestedScopes,
                $"Write scopes requested by spawn of '{agentName}'.",
                cancellationToken).ConfigureAwait(false);
            child.LeaseId = leaseResult.Lease?.LeaseId;
            child.FencingToken = leaseResult.Lease?.FencingToken;

            await EmitOnParentAsync(
                context,
                leaseResult.Ok ? "reservation.granted" : "reservation.denied",
                new Dictionary<string, object?>
                {
                    ["childSessionId"] = sessionId,
                    ["leaseId"] = leaseResult.Lease?.LeaseId,
                    ["error"] = leaseResult.Error?.Code,
                },
                cancellationToken).ConfigureAwait(false);
        }

        if (requestedScopes.Length > 0
            && adapter.RuntimeKind == AgentRuntimeKinds.ClaudeCode
            && leaseResult?.Lease is null)
        {
            children.TryRemove(agentName, out _);
            _childrenBySession.TryRemove(sessionId, out _);
            await _identities.ReleaseAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            UntrackChild(sessionId);
            await AppendChildEventAsync(
                child,
                "child.failed",
                new Dictionary<string, object?>
                {
                    ["agentName"] = agentName,
                    ["parentSessionId"] = context.SessionId,
                    ["status"] = ChildAgentStatus.Failed,
                    ["reason"] = "Claude write access requires an acquired reservation lease.",
                },
                CancellationToken.None).ConfigureAwait(false);
            return SpawnFailure(
                "reservation_required",
                "Claude write access requires an acquired lease and fencing token.");
        }

        AgentRuntimeAuthorizationContext? authorization = null;
        if (leaseResult?.Lease is { } lease)
        {
            authorization = new AgentRuntimeAuthorizationContext(lease.LeaseId, lease.FencingToken);
        }

        AgentSessionHandle? handle = null;
        try
        {
            var start = new AgentStartRequest(
                sessionId,
                new ProjectId(Guid.Parse(context.ProjectId)),
                new WorkRequestId(Guid.Parse(context.RequestId)),
                context.SessionId,
                agentName,
                spec.Role,
                context.RepositoryRoot!,
                spec.Prompt,
                AgentRuntimeMode.Child,
                model,
                authorization);
            handle = await adapter.StartAsync(start, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            children.TryRemove(agentName, out _);
            _childrenBySession.TryRemove(sessionId, out _);
            await _identities.ReleaseAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            UntrackChild(sessionId);
            if (leaseResult?.Lease is { } failedLease)
            {
                await _reservations.ReleaseAsync(
                    failedLease.LeaseId,
                    Guid.Parse(context.ProjectId),
                    sessionId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            await AppendChildEventAsync(
                child,
                "child.failed",
                new Dictionary<string, object?>
                {
                    ["agentName"] = agentName,
                    ["parentSessionId"] = context.SessionId,
                    ["status"] = ChildAgentStatus.Failed,
                    ["reason"] = ex.Message,
                },
                CancellationToken.None).ConfigureAwait(false);
            return SpawnFailure("child_start_failed", $"Child '{agentName}' failed to start: {ex.Message}");
        }

        child.Adapter = adapter;
        child.MarkStarted();
        if (child.LeaseId is not null && child.TryBeginLeaseRenewal())
        {
            _ = RenewLeaseLoopAsync(child);
        }

        await EmitOnParentAsync(
            context,
            "child.started",
            new Dictionary<string, object?>
            {
                ["childSessionId"] = sessionId,
                ["providerSessionId"] = handle.ProviderSessionId,
                ["agentName"] = agentName,
                ["role"] = spec.Role,
                ["model"] = model,
                ["runtime"] = adapter.RuntimeKind,
                ["parentSessionId"] = context.SessionId,
                ["leaseId"] = leaseResult?.Lease?.LeaseId,
            },
            cancellationToken).ConfigureAwait(false);

        _ = WatchChildAsync(child);

        return new Dictionary<string, object?>
        {
            ["agentName"] = agentName,
            ["childSessionId"] = sessionId,
            ["parentSessionId"] = context.SessionId,
            ["role"] = spec.Role,
            ["model"] = model,
            ["runtime"] = adapter.RuntimeKind,
            ["leaseId"] = leaseResult?.Lease?.LeaseId,
            ["fencingToken"] = leaseResult?.Lease?.FencingToken,
            ["reservationError"] = leaseResult?.Error?.Code,
        };
    }

    private static Dictionary<string, object?> SpawnFailure(string code, string message) => new()
    {
        ["ok"] = false,
        ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
    };

    // ---- Status / await / cancel ----

    private PiToolResponse Status(PiOrchestrationContext context, JsonElement? payload)
    {
        var selected = SelectChildren(context, payload);
        if (selected is null)
        {
            return PiToolResponse.Success(Array.Empty<object?>());
        }

        return PiToolResponse.Success(selected.Select(ToStatusView).ToArray());
    }

    private async Task<PiToolResponse> AwaitAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        var child = SelectChild(context, payload);
        if (child is null)
        {
            return PiToolResponse.Failure("unknown_agent", "No such child agent for this request.");
        }

        var timeout = TimeSpan.FromSeconds(
            Math.Clamp(payload.GetInt32Property("timeoutSeconds") ?? 300, 1, 3600));
        try
        {
            var terminal = await child.Terminal.Task
                .WaitAsync(timeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            return PiToolResponse.Success(TerminalView(child, terminal));
        }
        catch (TimeoutException)
        {
            return PiToolResponse.Failure(
                "await_timeout",
                $"Child '{child.AgentName}' did not reach a terminal state within {timeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<PiToolResponse> CancelAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        var child = SelectChild(context, payload);
        if (child is null)
        {
            return PiToolResponse.Failure("unknown_agent", "No such child agent for this request.");
        }

        if (child.IsTerminal)
        {
            return PiToolResponse.Failure("agent_not_running", "The child agent already reached a terminal state.");
        }

        child.RequestCancel();
        SecurityAuditLog.Cancellation(_logger, child.SessionId, context.SessionId, "cancelled_by_request");

        if (child.IsTerminal)
        {
            // The watch loop already observed the close/crash and emitted the terminal event.
            return PiToolResponse.Success(TerminalView(child, child.Terminal.Task.Result));
        }

        // Claim the cancelled terminal up front so a protocol-cancel timeout or the resulting
        // close cannot race the watch loop into recording the child as failed.
        await EmitTerminalAsync(child, ChildAgentStatus.Cancelled, "cancelled_by_request")
            .ConfigureAwait(false);
        var cancelTerminal = new ChildTerminal(ChildAgentStatus.Cancelled, "cancelled_by_request");
        child.Terminal.TrySetResult(cancelTerminal);
        await ReleaseChildAsync(child).ConfigureAwait(false);

        if (child.Adapter is not null)
        {
            try
            {
                await child.Adapter.CancelAsync(child.SessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Cancel of child {ChildSessionId} failed; closing forcibly.", child.SessionId);
            }
        }

        await child.CloseAsync().ConfigureAwait(false);
        return PiToolResponse.Success(TerminalView(child, cancelTerminal));
    }

    /// <summary>
    /// Control-plane initiated cancel (node hub <c>CancelSession</c>): cancels the child with
    /// the given session id when this node hosts it. Root sessions are the root supervisor's
    /// responsibility.
    /// </summary>
    public async Task<bool> CancelSessionAsync(string sessionId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!_childrenBySession.TryGetValue(sessionId, out var child) || child.IsTerminal)
        {
            return false;
        }

        child.RequestCancel();
        await EmitTerminalAsync(child, ChildAgentStatus.Cancelled, reason).ConfigureAwait(false);
        child.Terminal.TrySetResult(new ChildTerminal(ChildAgentStatus.Cancelled, reason));
        await child.CloseAsync().ConfigureAwait(false);
        await ReleaseChildAsync(child).ConfigureAwait(false);
        return true;
    }

    // ---- Mail ----

    private async Task<PiToolResponse> SendMessageAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        var recipients = payload.GetStringListProperty("recipients");
        if (recipients is null || recipients.Count == 0)
        {
            return PiToolResponse.Failure(
                "recipients_required", "recipients must be a non-empty string array.");
        }

        var importance = payload?.GetStringProperty("importance") ?? "normal";
        if (importance is not ("normal" or "high"))
        {
            return PiToolResponse.Failure(
                "invalid_importance", "Importance must be 'normal' or 'high'.");
        }

        var delivery = await _mail.SendAsync(
            Guid.Parse(context.ProjectId),
            Guid.Parse(context.RequestId),
            payload.GetStringProperty("threadId"),
            context.SessionId,
            recipients,
            payload.GetStringProperty("subject") ?? string.Empty,
            payload.GetStringProperty("body") ?? payload.GetStringProperty("bodyMarkdown") ?? string.Empty,
            importance,
            payload.GetBooleanProperty("ackRequired") ?? false,
            payload.GetStringProperty("inReplyToMessageId"),
            cancellationToken).ConfigureAwait(false);

        await EmitOnParentAsync(
            context,
            "mail.sent",
            new Dictionary<string, object?>
            {
                ["messageId"] = delivery.MessageId,
                ["threadId"] = delivery.ThreadId,
                ["recipients"] = delivery.Recipients,
            },
            cancellationToken).ConfigureAwait(false);
        return PiToolResponse.Success(new Dictionary<string, object?>
        {
            ["messageId"] = delivery.MessageId,
            ["threadId"] = delivery.ThreadId,
            ["recipients"] = delivery.Recipients,
        });
    }

    private async Task<PiToolResponse> ReadInboxAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        var threadId = payload.GetStringProperty("threadId");
        var maxCount = payload.GetInt32Property("maxCount") ?? 50;
        var inbox = threadId is null
            ? await _mail.FetchInboxAsync(Guid.Parse(context.ProjectId), context.SessionId, maxCount, cancellationToken).ConfigureAwait(false)
            : await _mail.FetchThreadAsync(Guid.Parse(context.ProjectId), context.SessionId, threadId, cancellationToken).ConfigureAwait(false);
        return PiToolResponse.Success(inbox.Messages
            .Select(m => (object?)new Dictionary<string, object?>
            {
                ["messageId"] = m.MessageId,
                ["senderSessionId"] = m.SenderSessionId,
                ["threadId"] = m.ThreadId,
                ["subject"] = m.Subject,
                ["body"] = m.BodyMarkdown,
                ["importance"] = m.Importance,
                ["ackRequired"] = m.AckRequired,
                ["sentAt"] = m.SentAt,
            })
            .ToArray());
    }

    private async Task<PiToolResponse> AcknowledgeAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        var messageId = payload.GetStringProperty("messageId");
        if (messageId is null)
        {
            return PiToolResponse.Failure("message_id_required", "messageId must be a non-empty string.");
        }

        var receipt = await _mail.AcknowledgeAsync(context.SessionId, messageId, cancellationToken)
            .ConfigureAwait(false);
        await _mail.MarkReadAsync(context.SessionId, messageId, cancellationToken).ConfigureAwait(false);
        await EmitOnParentAsync(
            context,
            "mail.acknowledged",
            new Dictionary<string, object?> { ["messageId"] = receipt.MessageId },
            cancellationToken).ConfigureAwait(false);
        return PiToolResponse.Success(new Dictionary<string, object?>
        {
            ["messageId"] = receipt.MessageId,
            ["acknowledged"] = true,
        });
    }

    // ---- Reservation lifecycle ----

    private async Task<PiToolResponse> AcquireAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        var scopes = ParseScopes(payload, out var error);
        if (error is not null)
        {
            return PiToolResponse.Failure(error.Code, error.Message);
        }

        var result = await _reservations.AcquireAsync(
            Guid.Parse(context.ProjectId),
            Guid.Parse(context.RequestId),
            context.SessionId,
            scopes!,
            payload.GetStringProperty("reason") ?? "reservation.acquire",
            cancellationToken).ConfigureAwait(false);
        TrackLeaseForChild(context.SessionId, result);
        return LeaseResponse(context, result, cancellationToken);
    }

    private async Task<PiToolResponse> ExpandAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        var lease = ParseLease(payload, out var error);
        var scopes = error is null ? ParseScopes(payload, out error) : null;
        if (error is not null)
        {
            return PiToolResponse.Failure(error.Code, error.Message);
        }

        var resolvedLease = lease!;


        var result = await _reservations.ExpandAsync(
            resolvedLease.LeaseId,
            Guid.Parse(context.ProjectId),
            resolvedLease.FencingToken,
            context.SessionId,
            scopes!,
            cancellationToken).ConfigureAwait(false);
        TrackLeaseForChild(context.SessionId, result);
        return LeaseResponse(context, result, cancellationToken);
    }

    private async Task<PiToolResponse> ReleaseAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        var lease = ParseLease(payload, out var error);
        if (error is not null)
        {
            return PiToolResponse.Failure(error.Code, error.Message);
        }

        var resolvedLease = lease!;


        var result = await _reservations.ReleaseAsync(
            resolvedLease.LeaseId,
            Guid.Parse(context.ProjectId),
            context.SessionId,
            cancellationToken).ConfigureAwait(false);
        if (result.Ok && _childrenBySession.TryGetValue(context.SessionId, out var releasing))
        {
            releasing.LeaseId = null;
            releasing.FencingToken = null;
        }
        await EmitOnParentAsync(
            context,
            result.Ok ? "reservation.released" : "reservation.recovery_required",
            new Dictionary<string, object?>
            {
                ["leaseId"] = resolvedLease.LeaseId,
                ["error"] = result.Error?.Code,
            },
            cancellationToken).ConfigureAwait(false);
        return LeaseResponse(context, result, cancellationToken);
    }

    /// <summary>
    /// Starts a handoff: persists <c>reservation.handoff_requested</c> and notifies the
    /// current owner. Ownership moves only when that owner accepts the target's request.
    /// </summary>
    private async Task<PiToolResponse> RequestHandoffAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        var toSessionId = payload.GetStringProperty("agentSessionId");
        if (string.IsNullOrWhiteSpace(toSessionId) && context.ParentSessionId is not null)
        {
            toSessionId = context.SessionId;
        }

        var paths = payload.GetStringListProperty("paths");
        if (string.IsNullOrWhiteSpace(toSessionId) || paths is null || paths.Count == 0)
        {
            return PiToolResponse.Failure(
                "handoff_request_invalid",
                "At least one repository-relative path is required; root callers must also provide agentSessionId.");
        }

        var leases = await _reservations.ListAsync(
            Guid.Parse(context.ProjectId), includeReleased: false, cancellationToken).ConfigureAwait(false);
        var lease = leases.FirstOrDefault(candidate =>
            string.Equals(candidate.State, "Active", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(candidate.OwnerSessionId, toSessionId, StringComparison.Ordinal)
            && paths.All(path => candidate.Scopes.Any(scope => ScopeContains(scope, path))));
        if (lease is null)
        {
            return PiToolResponse.Failure(
                "handoff_lease_not_found",
                "No single active lease covers every requested path.");
        }

        _pendingHandoffs[lease.LeaseId] = (lease.OwnerSessionId, toSessionId);
        await EmitOnParentAsync(
            context,
            "reservation.handoff_requested",
            new Dictionary<string, object?>
            {
                ["leaseId"] = lease.LeaseId,
                ["fromSessionId"] = lease.OwnerSessionId,
                ["toSessionId"] = toSessionId,
                ["paths"] = paths,
                ["reason"] = payload.GetStringProperty("reason"),
            },
            cancellationToken).ConfigureAwait(false);
        await _mail.SendAsync(
            Guid.Parse(context.ProjectId),
            Guid.Parse(context.RequestId),
            null,
            context.SessionId,
            [lease.OwnerSessionId],
            "Reservation handoff requested",
            $"Session {toSessionId} requests lease {lease.LeaseId}. "
            + "As the current owner, call accept_reservation_handoff with the lease id to transfer ownership.",
            "high",
            ackRequired: true,
            inReplyToMessageId: null,
            cancellationToken).ConfigureAwait(false);
        return PiToolResponse.Success(new Dictionary<string, object?>
        {
            ["leaseId"] = lease.LeaseId,
            ["state"] = "handoff_requested",
            ["toSessionId"] = toSessionId,
        });
    }

    /// Accepts a pending handoff as the current owner: transfers ownership to the
    /// requesting target through the reservation authority and persists the result.
    /// </summary>
    private async Task<PiToolResponse> AcceptHandoffAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(payload.GetStringProperty("leaseId"), out var leaseId))
        {
            return PiToolResponse.Failure("lease_required", "leaseId must be a GUID string.");
        }

        if (!_pendingHandoffs.TryGetValue(leaseId, out var handoff)
            || handoff.FromSessionId != context.SessionId
            || !_pendingHandoffs.TryRemove(leaseId, out handoff))
        {
            return PiToolResponse.Failure(
                "handoff_not_pending",
                $"No handoff of lease {leaseId} is pending for acceptance by this session.");
        }

        var result = await _reservations.TransferAsync(
            leaseId, context.SessionId, handoff.ToSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (result.Ok && _childrenBySession.TryGetValue(context.SessionId, out var source))
        {
            source.LeaseId = null;
            source.FencingToken = null;
        }
        TrackLeaseForChild(handoff.ToSessionId, result);
        await EmitOnParentAsync(
            context,
            result.Ok ? "reservation.transferred" : "reservation.recovery_required",
            new Dictionary<string, object?>
            {
                ["leaseId"] = leaseId,
                ["fromSessionId"] = context.SessionId,
                ["toSessionId"] = handoff.ToSessionId,
                ["error"] = result.Error?.Code,
            },
            cancellationToken).ConfigureAwait(false);
        return LeaseResponse(context, result, cancellationToken);
    }

    private PiToolResponse LeaseResponse(
        PiOrchestrationContext context,
        ReservationOperationResult result,
        CancellationToken cancellationToken)
    {
        if (result.Lease is { } lease)
        {
            _ = EmitOnParentAsync(
                context,
                "reservation.granted",
                new Dictionary<string, object?> { ["leaseId"] = lease.LeaseId },
                cancellationToken);
            return PiToolResponse.Success(new Dictionary<string, object?>
            {
                ["leaseId"] = lease.LeaseId,
                ["fencingToken"] = lease.FencingToken,
                ["state"] = lease.State,
                ["expiresAt"] = lease.ExpiresAt,
                ["scopes"] = lease.Scopes,
            });
        }

        return PiToolResponse.Failure(
            result.Error?.Code ?? "reservation_error",
            result.Error?.Message ?? "The reservation operation failed.");
    }

    private void TrackLeaseForChild(string sessionId, ReservationOperationResult result)
    {
        if (result.Lease is not { } lease
            || !_childrenBySession.TryGetValue(sessionId, out var child))
        {
            return;
        }

        child.LeaseId = lease.LeaseId;
        child.FencingToken = lease.FencingToken;
        if (child.TryBeginLeaseRenewal())
        {
            _ = RenewLeaseLoopAsync(child);
        }
    }

    private static bool ScopeContains(ReservationScopeSpec scope, string path)
    {
        var candidate = path.Replace('\\', '/').Trim('/');
        var reserved = scope.Path.Replace('\\', '/').Trim('/');
        return scope.Kind.Equals("directory", StringComparison.OrdinalIgnoreCase)
            ? candidate.Equals(reserved, StringComparison.Ordinal)
                || candidate.StartsWith(reserved + "/", StringComparison.Ordinal)
            : candidate.Equals(reserved, StringComparison.Ordinal);
    }

    // ---- Reserved filesystem mutations ----

    private async Task<PiToolResponse> ReservedReadAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        var lease = ParseLease(payload, out var error);
        var path = error is null ? payload.GetStringProperty("path") : null;
        if (error is null && path is null)
        {
            error = new GatewayError("path_required", "path must be a non-empty string.");
        }

        if (error is not null)
        {
            return PiToolResponse.Failure(error.Code, error.Message);
        }

        var resolvedLease = lease!;

        var (root, ops) = RequireRepository(context);
        var result = await ops.ReadTextAsync(root, resolvedLease, context.SessionId, path!, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Ok)
        {
            return FileResponse(result);
        }

        return PiToolResponse.Success(new Dictionary<string, object?>
        {
            ["path"] = path,
            ["content"] = ReservedFileOperations.ReadContent(result),
        });
    }

    private async Task<PiToolResponse> RunSinglePathMutationAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        Func<ReservedFileOperations, Guid, string, MutationLease, string, string, CancellationToken, Task<FileOperationResult>> run,
        CancellationToken cancellationToken)
    {
        var lease = ParseLease(payload, out var error);
        var path = error is null ? payload.GetStringProperty("path") : null;
        if (error is null && path is null)
        {
            error = new GatewayError("path_required", "path must be a non-empty string.");
        }

        if (error is not null)
        {
            return PiToolResponse.Failure(error.Code, error.Message);
        }

        var resolvedLease = lease!;

        if (!Guid.TryParse(context.RequestId, out var mutationRequestId))
        {
            return PiToolResponse.Failure("request_identity_unknown", "The session is not bound to a request.");
        }

        var (root, ops) = RequireRepository(context);
        var result = await run(ops, mutationRequestId, root, resolvedLease, context.SessionId, path!, cancellationToken)
            .ConfigureAwait(false);
        return FileResponse(result);
    }

    private async Task<PiToolResponse> ReservedMoveAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        var lease = ParseLease(payload, out var error);
        var source = error is null ? payload.GetStringProperty("source") : null;
        var destination = error is null ? payload.GetStringProperty("destination") : null;
        if (error is null && source is null)
        {
            error = new GatewayError("path_required", "source must be a non-empty string.");
        }

        if (error is null && destination is null)
        {
            error = new GatewayError("path_required", "destination must be a non-empty string.");
        }

        if (error is not null)
        {
            return PiToolResponse.Failure(error.Code, error.Message);
        }

        var resolvedLease = lease!;


        if (!Guid.TryParse(context.RequestId, out var moveRequestId))
        {
            return PiToolResponse.Failure("request_identity_unknown", "The session is not bound to a request.");
        }

        var (root, ops) = RequireRepository(context);
        var result = await ops.MoveAsync(
            moveRequestId, root, resolvedLease, context.SessionId, source!, destination!, cancellationToken)
            .ConfigureAwait(false);
        return FileResponse(result);
    }

    private (string Root, ReservedFileOperations Ops) RequireRepository(PiOrchestrationContext context)
    {
        if (string.IsNullOrEmpty(context.RepositoryRoot))
        {
            throw new InvalidOperationException("The session has no repository root bound.");
        }

        return (context.RepositoryRoot, _fileOperations);
    }

    private static PiToolResponse FileResponse(FileOperationResult result)
        => result.Ok
            ? PiToolResponse.Success(new Dictionary<string, object?> { ["ok"] = true })
            : PiToolResponse.Failure(
                result.ErrorCode ?? "mutation_denied",
                result.ErrorMessage ?? "The mutation was not applied.");

    // ---- Child watch loop and terminal events ----

    private async Task WatchChildAsync(ChildAgent child)
    {
        var blocked = false;
        try
        {
            await foreach (var sessionEvent in child.Adapter!.WatchAsync(child.SessionId, _disposeCts.Token)
                .ConfigureAwait(false))
            {
                await AppendChildEventAsync(
                    child,
                    sessionEvent.Type,
                    sessionEvent.Payload,
                    CancellationToken.None).ConfigureAwait(false);
                if (sessionEvent.Type == "session.snapshot"
                    && sessionEvent.Payload.TryGetValue("workState", out var workState)
                    && string.Equals(
                        workState?.ToString(),
                        nameof(AgentWorkState.Blocked),
                        StringComparison.OrdinalIgnoreCase))
                {
                    blocked = true;
                }


                if (sessionEvent.Type is "session.completed"
                    or "session.closed"
                    or "session.failed")
                {
                    if (child.IsTerminal)
                    {
                        // Terminal already emitted (e.g. by an explicit cancel).
                        return;
                    }

                    var failed = sessionEvent.Type == "session.failed";
                    var completed = sessionEvent.Type == "session.completed";
                    var status = failed
                        ? ChildAgentStatus.Failed
                        : blocked
                            ? ChildAgentStatus.Blocked
                            : child.CancelRequested && !completed
                                ? ChildAgentStatus.Cancelled
                                : ChildAgentStatus.Completed;
                    var reason = sessionEvent.Payload.TryGetValue("reason", out var reasonValue)
                        ? reasonValue?.ToString() ?? "unknown"
                        : "worker_closed";
                    if (failed)
                    {
                        await NotifyCrashAsync(child, reason).ConfigureAwait(false);
                    }

                    await child.CloseAsync().ConfigureAwait(false);
                    await EmitTerminalAsync(child, status, reason).ConfigureAwait(false);
                    child.Terminal.TrySetResult(new ChildTerminal(status, reason));
                    await ReleaseChildAsync(child).ConfigureAwait(false);
                    return;
                }
            }

            await NotifyCrashAsync(child, "worker_stream_ended").ConfigureAwait(false);
            await child.CloseAsync().ConfigureAwait(false);
            await EmitTerminalAsync(child, ChildAgentStatus.Failed, "worker_stream_ended")
                .ConfigureAwait(false);
            child.Terminal.TrySetResult(new ChildTerminal(ChildAgentStatus.Failed, "worker_stream_ended"));
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            child.Terminal.TrySetResult(new ChildTerminal(ChildAgentStatus.Cancelled, "node_shutdown"));
            UntrackChild(child.SessionId);
        }
        catch (Exception ex)
        {
            await NotifyCrashAsync(child, ex.Message).ConfigureAwait(false);
            await child.CloseAsync().ConfigureAwait(false);
            await EmitTerminalAsync(child, ChildAgentStatus.Failed, ex.Message).ConfigureAwait(false);
            child.Terminal.TrySetResult(new ChildTerminal(ChildAgentStatus.Failed, ex.Message));
            await ReleaseChildAsync(child).ConfigureAwait(false);
        }
    }

    private async Task NotifyCrashAsync(ChildAgent child, string reason)
    {
        if (!Guid.TryParse(child.NodeId, out var nodeId)
            || !Guid.TryParse(child.ProjectId, out var projectId))
        {
            return;
        }

        Guid? requestId = Guid.TryParse(child.RequestId, out var parsed) ? parsed : null;
        try
        {
            await _crashRecovery.MarkOwnedLeasesRecoveryRequiredAsync(
                nodeId, projectId, requestId, child.SessionId, reason, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark leases recovery-required for {SessionId}.", child.SessionId);
        }

        try
        {
            await _mail.SendAsync(
                projectId,
                requestId ?? Guid.Empty,
                threadId: null,
                senderSessionId: child.SessionId,
                recipients: [child.ParentSessionId],
                subject: "Child runtime crash",
                bodyMarkdown: $"Child '{child.AgentName}' failed: {reason}",
                importance: "high",
                ackRequired: true,
                inReplyToMessageId: null,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mail parent about crash of {SessionId}.", child.SessionId);
        }
    }

    private async Task EmitTerminalAsync(ChildAgent child, string status, string reason)
    {
        child.MarkTerminal(status);
        var type = status switch
        {
            ChildAgentStatus.Completed => "child.completed",
            ChildAgentStatus.Failed => "child.failed",
            ChildAgentStatus.Blocked => "child.blocked",
            _ => "child.cancelled",
        };
        UntrackChild(child.SessionId);

        await AppendChildEventAsync(
            child,
            type,
            new Dictionary<string, object?>
            {
                ["agentName"] = child.AgentName,
                ["role"] = child.Role,
                ["model"] = child.Model,
                ["parentSessionId"] = child.ParentSessionId,
                ["status"] = status,
                ["reason"] = reason,
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Ends the child's admission-gate activity exactly once, if it was tracked.</summary>
    private void UntrackChild(string sessionId)
    {
        if (_childActivity.TryRemove(sessionId, out var activity))
        {
            activity.Dispose();
        }
    }

    /// <summary>
    /// Renews the child's active lease on a fixed interval while the child runs; the fencing
    /// token is refreshed from each successful renewal. Stops on terminal or node shutdown.
    /// </summary>
    private async Task RenewLeaseLoopAsync(ChildAgent child)
    {
        var interval = TimeSpan.FromSeconds(_workerOptions.LeaseRenewalSeconds);
        try
        {
            while (!child.IsTerminal)
            {
                await Task.Delay(interval, child.RenewalCts.Token).ConfigureAwait(false);
                if (child.IsTerminal || child.LeaseId is not { } leaseId || child.FencingToken is not { } token)
                {
                    continue;
                }

                try
                {
                    var result = await _reservations.RenewAsync(
                        leaseId, token, child.SessionId, child.RenewalCts.Token).ConfigureAwait(false);
                    if (result.Lease is { } renewed)
                    {
                        child.FencingToken = renewed.FencingToken;
                    }
                    else if (result.Error?.Code is "not_found" or "invalid_state")
                    {
                        await EmitTerminalAsync(child, ChildAgentStatus.Failed, $"lease_{result.Error.Code}")
                            .ConfigureAwait(false);
                        child.Terminal.TrySetResult(new ChildTerminal(
                            ChildAgentStatus.Failed, $"lease_{result.Error.Code}"));
                        return;
                    }
                }
                catch (OperationCanceledException) when (child.RenewalCts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Lease renewal for child {ChildSessionId} failed transiently; retrying.",
                        child.SessionId);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Releases the child's lease (voluntary release on graceful terminals) and its
    /// project-scoped mail identity; idempotent and safe to call once per terminal.
    /// </summary>
    private async Task ReleaseChildAsync(ChildAgent child)
    {
        child.RenewalCts.Cancel();
        try
        {
            if (child.LeaseId is { } leaseId
                && child.Status is not (ChildAgentStatus.Failed or ChildAgentStatus.Cancelled))
            {
                await _reservations.ReleaseAsync(
                    leaseId, Guid.Parse(child.ProjectId), child.SessionId, CancellationToken.None)
                    .ConfigureAwait(false);
                child.LeaseId = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Lease release for child {ChildSessionId} failed.", child.SessionId);
        }

        try
        {
            await _identities.ReleaseAsync(child.SessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Identity release for child {ChildSessionId} failed.", child.SessionId);
        }
    }

    /// <summary>Appends one event to the child's own durable spool stream.</summary>
    private async Task AppendChildEventAsync(
        ChildAgent child,
        string type,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.Parse(child.RequestId);
        var projectId = Guid.Parse(child.ProjectId);
        if (!_assignmentCredentials.TryGetByRequest(requestId, out var credential)
            || credential.RequestId != requestId
            || credential.ProjectId != projectId)
        {
            throw new InvalidOperationException(
                $"No active assignment credential is available for request {requestId} in project {projectId}.");
        }

        var sequence = child.AllocateSequence();
        var normalized = new Dictionary<string, object?>(payload)
        {
            ["runtime"] = child.RuntimeKind,
            ["childSessionId"] = child.SessionId,
            ["parentSessionId"] = child.ParentSessionId,
            ["agentName"] = child.AgentName,
            ["role"] = child.Role,
            ["model"] = child.Model,
        };
        var message = new NodeEventMessage(
            EventId: $"{child.SessionId}-{sequence}-{type}",
            NodeId: Guid.Parse(child.NodeId),
            ProjectId: projectId,
            RequestId: requestId,
            ClaimToken: credential.ClaimToken,
            SessionId: child.SessionId,
            Sequence: sequence,
            Type: type,
            OccurredAt: _timeProvider.GetUtcNow(),
            PayloadJson: JsonSerializer.Serialize(normalized, JsonOptions));

        await _spool.AppendAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task EmitOnParentAsync(
        PiOrchestrationContext context,
        string type,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
        => await context.EmitAsync(type, payload, cancellationToken).ConfigureAwait(false);

    // ---- Selection and payload helpers ----

    private IReadOnlyList<ChildAgent> SelectChildren(PiOrchestrationContext context, JsonElement? payload)
    {
        var children = _childrenByRequest.TryGetValue(context.RequestId, out var byRequest)
            ? byRequest
            : null;
        if (children is null)
        {
            return [];
        }

        var name = payload.GetStringProperty("agentName");
        var childSessionId = payload.GetStringProperty("childSessionId")
            ?? payload.GetStringProperty("sessionId");
        if (name is not null)
        {
            return children.TryGetValue(name, out var byName) ? [byName] : [];
        }

        if (childSessionId is not null)
        {
            if (!_childrenBySession.TryGetValue(childSessionId, out var bySession)
                || !IsOwnedByCaller(context, bySession)
                || !children.ContainsKey(bySession.AgentName))
            {
                return [];
            }

            return [bySession];
        }

        return [.. children.Values];
    }

    private ChildAgent? SelectChild(PiOrchestrationContext context, JsonElement? payload)
    {
        var selected = SelectChildren(context, payload);
        return selected.Count == 1 ? selected[0] : null;
    }

    private static bool IsOwnedByCaller(PiOrchestrationContext context, ChildAgent child)
        => string.Equals(child.RequestId, context.RequestId, StringComparison.Ordinal)
            && string.Equals(child.ProjectId, context.ProjectId, StringComparison.Ordinal)
            && string.Equals(child.ParentSessionId, context.SessionId, StringComparison.Ordinal);

    private static Dictionary<string, object?> ToStatusView(ChildAgent child) => new()
    {
        ["agentName"] = child.AgentName,
        ["childSessionId"] = child.SessionId,
        ["role"] = child.Role,
        ["model"] = child.Model,
        ["parentSessionId"] = child.ParentSessionId,
        ["status"] = child.Status,
        ["startedAt"] = child.StartedAt,
    };

    private static Dictionary<string, object?> TerminalView(ChildAgent child, ChildTerminal terminal) => new()
    {
        ["agentName"] = child.AgentName,
        ["childSessionId"] = child.SessionId,
        ["status"] = terminal.Status,
        ["reason"] = terminal.Reason,
    };

    private static List<SpawnSpec> ParseSpawnSpecs(
        JsonElement? payload,
        bool single,
        out GatewayError? error)
    {
        error = null;
        var specs = new List<SpawnSpec>();
        if (payload is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            error = new GatewayError("payload_invalid", "A spawn request requires a JSON object payload.");
            return specs;
        }

        if (element.GetPropertyOrNull("agents") is { } agents)
        {
            if (agents.ValueKind != JsonValueKind.Array)
            {
                error = new GatewayError("payload_invalid", "'agents' must be an array.");
                return specs;
            }

            foreach (var agent in agents.EnumerateArray())
            {
                if (!TryParseSpawnSpec(agent, out error, out var spec))
                {
                    return specs;
                }

                specs.Add(spec);
            }

            return specs;
        }

        if (!single)
        {
            error = new GatewayError("payload_invalid", "spawn_agents requires an 'agents' array.");
            return specs;
        }

        if (!TryParseSpawnSpec(element, out error, out var one))
        {
            return specs;
        }

        specs.Add(one);
        return specs;
    }

    private static bool TryParseSpawnSpec(
        JsonElement element,
        out GatewayError? error,
        out SpawnSpec spec)
    {
        error = null;
        spec = null!;
        foreach (var field in new[] { "agentName", "role", "prompt" })
        {
            if (string.IsNullOrWhiteSpace(element.GetStringProperty(field)))
            {
                error = new GatewayError("payload_invalid", $"agent.{field} must be a non-empty string.");
                return false;
            }
        }

        var scopes = element.GetPropertyOrNull("requestedWriteScopes");
        List<ReservationScopeSpec> scopeList = [];
        if (scopes is { } scopeElement)
        {
            if (scopeElement.ValueKind != JsonValueKind.Array)
            {
                error = new GatewayError("payload_invalid", "requestedWriteScopes must be an array.");
                return false;
            }

            foreach (var scope in scopeElement.EnumerateArray())
            {
                var kind = scope.GetStringProperty("kind") ?? "file";
                var path = scope.GetStringProperty("path");
                if (path is null)
                {
                    error = new GatewayError(
                        "payload_invalid", "Every requested write scope needs a non-empty 'path'.");
                    return false;
                }

                scopeList.Add(new ReservationScopeSpec(kind, path));
            }
        }

        spec = new SpawnSpec(
            element.GetStringProperty("agentName")!,
            element.GetStringProperty("role")!,
            element.GetStringProperty("prompt")!,
            scopeList);
        return true;
    }

    private static List<ReservationScopeSpec>? ParseScopes(JsonElement? payload, out GatewayError? error)
    {
        error = null;
        if (payload is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            error = new GatewayError("payload_invalid", "A JSON object payload is required.");
            return null;
        }

        var scopes = element.GetPropertyOrNull("scopes");
        if (scopes is not { ValueKind: JsonValueKind.Array })
        {
            error = new GatewayError("scopes_required", "scopes must be a non-empty array.");
            return null;
        }

        List<ReservationScopeSpec> result = [];
        foreach (var scope in scopes.Value.EnumerateArray())
        {
            var path = scope.GetStringProperty("path");
            if (path is null)
            {
                error = new GatewayError("payload_invalid", "Every scope needs a non-empty 'path'.");
                return null;
            }

            result.Add(new ReservationScopeSpec(scope.GetStringProperty("kind") ?? "file", path));
        }

        if (result.Count == 0)
        {
            error = new GatewayError("scopes_required", "scopes must be a non-empty array.");
            return null;
        }

        return result;
    }

    private static PiToolResponse WorkspaceQuery(string? repositoryRoot, FileOperationResult result)
    {
        if (string.IsNullOrEmpty(repositoryRoot))
        {
            return PiToolResponse.Failure("repository_root_unknown", "The session has no repository bound.");
        }

        if (!result.Ok)
        {
            return PiToolResponse.Failure(result.ErrorCode ?? "path_denied", result.ErrorMessage ?? "Denied.");
        }

        var content = result is ReadResult read ? read.Content : string.Empty;
        return PiToolResponse.Success(new Dictionary<string, object?> { ["content"] = content });
    }

    private static MutationLease? ParseLease(JsonElement? payload, out GatewayError? error)
    {
        error = null;
        if (payload is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            error = new GatewayError("payload_invalid", "A JSON object payload is required.");
            return null;
        }

        if (!Guid.TryParse(element.GetStringProperty("leaseId"), out var leaseId))
        {
            error = new GatewayError("lease_required", "leaseId must be a GUID string.");
            return null;
        }

        var fencingToken = element.GetInt64Property("fencingToken");
        if (fencingToken is null)
        {
            error = new GatewayError("fencing_token_required", "fencingToken must be a number.");
            return null;
        }

        return new MutationLease(leaseId, fencingToken.Value);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _disposeCts.CancelAsync().ConfigureAwait(false);
        foreach (var child in _childrenBySession.Values)
        {
            child.Terminal.TrySetResult(new ChildTerminal(ChildAgentStatus.Cancelled, "node_shutdown"));
            await child.CloseAsync().ConfigureAwait(false);
            UntrackChild(child.SessionId);
            await ReleaseChildAsync(child).ConfigureAwait(false);
        }

        _disposeCts.Dispose();
    }
}

/// <summary>One validated child spawn request from the worker payload.</summary>
public sealed record SpawnSpec(
    string AgentName,
    string Role,
    string Prompt,
    IReadOnlyList<ReservationScopeSpec> RequestedWriteScopes);
