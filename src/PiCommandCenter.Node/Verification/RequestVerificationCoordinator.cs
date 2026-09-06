using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Quiescence;
using PiCommandCenter.Node.Security;

namespace PiCommandCenter.Node.Verification;

/// <summary>
/// Resolves the assignment policy and owns admission, the build lease, execution, persistence,
/// lifecycle events, and final-result reuse for request verification.
/// </summary>
public sealed class RequestVerificationCoordinator : IRequestVerificationCoordinator
{
    private const int MaxSummaryLength = 2048;

    private readonly IOptions<VerificationOptions> _options;
    private readonly IBaselineVerification _baseline;
    private readonly IAdmittedVerificationCommandRunner _profiles;
    private readonly INodeReservationGateway _reservations;
    private readonly IRequestAdmissionGate _admission;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _requestGates = new();
    private readonly ConcurrentDictionary<Guid, List<VerificationRunDto>> _recordedRuns = new();
    private readonly ConcurrentDictionary<FinalResultKey, RequestVerificationDecision> _finalResults = new();

    public RequestVerificationCoordinator(
        IOptions<VerificationOptions> options,
        IBaselineVerification baseline,
        IAdmittedVerificationCommandRunner profiles,
        INodeReservationGateway reservations,
        IRequestAdmissionGate admission,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<RequestVerificationDecision> VerifyFinalAsync(
        RequestVerificationContext context,
        CancellationToken cancellationToken)
        => VerifyAsync(context, intermediate: false, cancellationToken);

    public Task<RequestVerificationDecision> VerifyIntermediateAsync(
        RequestVerificationContext context,
        CancellationToken cancellationToken)
        => VerifyAsync(context, intermediate: true, cancellationToken);

    public async Task<string> CaptureFingerprintAsync(
        RequestVerificationContext context,
        CancellationToken cancellationToken)
    {
        var policy = ResolvePolicy(context);
        try
        {
            return await _baseline.CaptureFingerprintAsync(
                CreateBaselineContext(context, policy),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new VerificationRejectedException(
                "verification_precondition_failed",
                BoundMessage(ex.Message));
        }
    }

    private async Task<RequestVerificationDecision> VerifyAsync(
        RequestVerificationContext context,
        bool intermediate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var gate = _requestGates.GetOrAdd(context.RequestId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await VerifyCoreAsync(context, intermediate, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RequestVerificationDecision> VerifyCoreAsync(
        RequestVerificationContext context,
        bool intermediate,
        CancellationToken cancellationToken)
    {
        RequestVerificationPolicy policy;
        try
        {
            policy = ResolvePolicy(context);
        }
        catch (VerificationRejectedException ex)
        {
            return await RejectAsync(context, intermediate, ex.Code, ex.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        VerificationProfileOptions? profile;
        try
        {
            profile = ResolveProfile(policy);
        }
        catch (VerificationRejectedException ex)
        {
            if (intermediate)
            {
                return await RejectAsync(context, true, ex.Code, ex.Message, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await FailUnavailablePolicyAsync(
                    context,
                    policy,
                    ex.Code,
                    ex.Message,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (intermediate && profile is null)
        {
            return await RejectAsync(
                context,
                intermediate: true,
                "no_project_checks",
                "This project has no configured project checks.",
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await RejectActiveSourceMutationAsync(context.ProjectId, cancellationToken).ConfigureAwait(false);
        }
        catch (VerificationRejectedException ex)
        {
            return await RejectAsync(context, intermediate, ex.Code, ex.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        using var operation = _admission.TryEnterOperation(
            context.RequestId,
            intermediate ? "verification:intermediate" : "verification:final");
        if (operation is null)
        {
            return await RejectAsync(
                context,
                intermediate,
                "admission_closed",
                "The request is terminalizing; no new verification work is admitted.",
                cancellationToken).ConfigureAwait(false);
        }

        ReservationLeaseInfo? buildLease = null;
        var started = false;
        string? fingerprint = null;
        try
        {
            // Once acquisition reaches the reservation authority, cancellation cannot safely
            // abandon the response: the lease id is required for guaranteed cleanup.
            var acquired = await _reservations.AcquireAsync(
                context.ProjectId,
                context.RequestId,
                context.RequestingSessionId,
                [new ReservationScopeSpec("resource", VerificationOptions.ProjectBuildResource)],
                intermediate ? "intermediate verification" : "final verification",
                CancellationToken.None).ConfigureAwait(false);
            if (!acquired.Ok || acquired.Lease is null)
            {
                return await RejectAsync(
                    context,
                    intermediate,
                    acquired.Error?.Code ?? "build_lease_denied",
                    acquired.Error?.Message ?? "Failed to acquire project-build.",
                    cancellationToken).ConfigureAwait(false);
            }

            buildLease = acquired.Lease;
            cancellationToken.ThrowIfCancellationRequested();
            await RejectActiveSourceMutationAsync(context.ProjectId, cancellationToken).ConfigureAwait(false);
            fingerprint = await _baseline.CaptureFingerprintAsync(
                CreateBaselineContext(context, policy),
                cancellationToken).ConfigureAwait(false);

            if (!intermediate
                && FindFinalResult(context, policy, fingerprint) is { } previous)
            {
                return previous.Kind == RequestVerificationDecisionKind.Passed
                    ? CreateDecision(
                        RequestVerificationDecisionKind.Reused,
                        "Reused final verification for the unchanged repository fingerprint.",
                        previous.Fingerprint,
                        previous.PolicyRevision,
                        previous.ErrorCode)
                    : previous;
            }

            var attemptId = Guid.NewGuid();
            if (!intermediate)
            {
                started = true;
                await context.EmitAsync(
                    "verification.started",
                    new Dictionary<string, object?>
                    {
                        ["fingerprint"] = fingerprint,
                        ["policyRevision"] = policy.Revision,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            var succeeded = true;
            var wasCancelled = false;
            if (!intermediate)
            {
                var baseline = await _baseline.RunAsync(
                    CreateBaselineContext(context, policy, fingerprint, VerificationRunKind.Baseline),
                    fingerprint,
                    cancellationToken).ConfigureAwait(false);
                await PersistAsync(
                    context,
                    policy,
                    fingerprint,
                    attemptId,
                    IBaselineVerification.ProfileId,
                    VerificationRunKind.Baseline,
                    baseline.Commands,
                    cancellationToken).ConfigureAwait(false);
                succeeded = baseline.Succeeded;
                wasCancelled = baseline.Commands.Any(command => command.Cancelled);
            }

            if (profile is not null && (intermediate || succeeded))
            {
                var profileId = EffectiveProfileId(profile, policy.TrustedProfileId!);
                var runKind = intermediate ? VerificationRunKind.Intermediate : VerificationRunKind.ProjectCheck;
                var profileRun = await _profiles.RunAdmittedAsync(
                    new VerificationRunContext(
                        context.ProjectId,
                        context.RequestId,
                        context.RequestingSessionId,
                        context.RepositoryRoot,
                        BindCommandStarting(context, policy, fingerprint, runKind)),
                    profileId,
                    cancellationToken).ConfigureAwait(false);
                await PersistAsync(
                    context,
                    policy,
                    fingerprint,
                    attemptId,
                    profileRun.ProfileId,
                    intermediate ? VerificationRunKind.Intermediate : VerificationRunKind.ProjectCheck,
                    profileRun.Commands,
                    cancellationToken).ConfigureAwait(false);
                succeeded &= profileRun.Succeeded;
                wasCancelled |= profileRun.Commands.Any(command => command.Cancelled);
            }

            var kind = wasCancelled
                ? RequestVerificationDecisionKind.Cancelled
                : succeeded
                    ? RequestVerificationDecisionKind.Passed
                    : RequestVerificationDecisionKind.Failed;
            var decision = CreateDecision(
                kind,
                wasCancelled
                    ? "Verification was cancelled."
                    : succeeded
                        ? intermediate ? "Project checks passed." : "Final verification passed."
                        : intermediate ? "Project checks failed." : "Final verification failed.",
                fingerprint,
                policy.Revision,
                succeeded ? null : wasCancelled ? "verification_cancelled" : "verification_failed");

            if (intermediate)
            {
                await EmitIntermediateAsync(context, decision, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!wasCancelled)
                {
                    _finalResults[new FinalResultKey(context.RequestId, fingerprint, policy.Revision)] = decision;
                }
                await context.EmitAsync(
                    wasCancelled
                        ? "verification.cancelled"
                        : succeeded
                            ? "verification.completed"
                            : "verification.failed",
                    EventPayload(decision),
                    cancellationToken).ConfigureAwait(false);
            }

            return decision;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = CreateDecision(
                RequestVerificationDecisionKind.Cancelled,
                "Verification was cancelled.",
                fingerprint,
                policy.Revision,
                "verification_cancelled");
            if (intermediate)
            {
                await EmitIntermediateAsync(context, cancelled, CancellationToken.None).ConfigureAwait(false);
            }
            else if (started)
            {
                await context.EmitAsync(
                    "verification.cancelled",
                    EventPayload(cancelled),
                    CancellationToken.None).ConfigureAwait(false);
            }

            return cancelled;
        }
        catch (VerificationRejectedException ex)
        {
            if (!started)
            {
                return await RejectAsync(context, intermediate, ex.Code, ex.Message, cancellationToken)
                    .ConfigureAwait(false);
            }

            var failed = CreateDecision(
                RequestVerificationDecisionKind.Failed,
                ex.Message,
                fingerprint,
                policy.Revision,
                ex.Code);
            if (fingerprint is not null)
            {
                _finalResults[new FinalResultKey(context.RequestId, fingerprint, policy.Revision)] = failed;
            }
            await context.EmitAsync(
                "verification.failed",
                EventPayload(failed),
                cancellationToken).ConfigureAwait(false);
            return failed;
        }
        catch (Exception ex)
        {
            if (!started)
            {
                return await RejectAsync(
                    context,
                    intermediate,
                    "verification_precondition_failed",
                    ex.Message,
                    cancellationToken).ConfigureAwait(false);
            }

            var failed = CreateDecision(
                RequestVerificationDecisionKind.Failed,
                ex.Message,
                fingerprint,
                policy.Revision,
                "verification_failed");
            if (fingerprint is not null)
            {
                _finalResults[new FinalResultKey(context.RequestId, fingerprint, policy.Revision)] = failed;
            }
            await context.EmitAsync(
                "verification.failed",
                EventPayload(failed),
                cancellationToken).ConfigureAwait(false);
            return failed;
        }
        finally
        {
            if (buildLease is not null)
            {
                try
                {
                    await _reservations.ReleaseAsync(
                        buildLease.LeaseId,
                        context.ProjectId,
                        context.RequestingSessionId,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The run result remains authoritative; lease expiry/reconciliation owns recovery.
                }
            }
        }
    }

    private RequestVerificationPolicy ResolvePolicy(RequestVerificationContext context)
    {
        if (context.ProjectId == Guid.Empty
            || context.RequestId == Guid.Empty
            || context.WorkspaceBindingId is not { } bindingId
            || bindingId == Guid.Empty
            || context.BindingValidationRevision is not > 0
            || string.IsNullOrWhiteSpace(context.RequestingSessionId)
            || string.IsNullOrWhiteSpace(context.RepositoryRoot)
            || string.IsNullOrWhiteSpace(context.BaselineCommit)
            || string.IsNullOrWhiteSpace(context.BaselineBranch))
        {
            throw new VerificationRejectedException(
                "verification_policy_unavailable",
                "The assignment does not contain a complete workspace verification snapshot.");
        }

        var policy = context.Policy ?? new RequestVerificationPolicy(
            $"baseline:{IBaselineVerification.Version}",
            IBaselineVerification.Version,
            null,
            null,
            [IBaselineVerification.RepositoryIntegrityCommandId]);
        if (string.IsNullOrWhiteSpace(policy.Revision)
            || !string.Equals(policy.BaselineVersion, IBaselineVerification.Version, StringComparison.Ordinal)
            || policy.MandatoryCommandIds.Count == 0
            || !policy.MandatoryCommandIds.Contains(
                IBaselineVerification.RepositoryIntegrityCommandId,
                StringComparer.Ordinal)
            || (string.IsNullOrWhiteSpace(policy.TrustedProfileId)
                != string.IsNullOrWhiteSpace(policy.TrustedProfileRevision)))
        {
            throw new VerificationRejectedException(
                "verification_policy_unavailable",
                "The assignment verification policy snapshot is incomplete or unsupported.");
        }

        return policy;
    }

    private VerificationProfileOptions? ResolveProfile(RequestVerificationPolicy policy)
    {
        if (policy.TrustedProfileId is null)
        {
            if (policy.MandatoryCommandIds.Count != 1
                || !string.Equals(
                    policy.MandatoryCommandIds[0],
                    IBaselineVerification.RepositoryIntegrityCommandId,
                    StringComparison.Ordinal))
            {
                throw new VerificationRejectedException(
                    "verification_policy_unavailable",
                    "The baseline-only policy contains unexpected mandatory commands.");
            }

            return null;
        }

        foreach (var (key, candidate) in _options.Value.Profiles)
        {
            if (!string.Equals(EffectiveProfileId(candidate, key), policy.TrustedProfileId, StringComparison.Ordinal))
            {
                continue;
            }
            if (candidate.Commands.Count == 0)
            {
                throw new VerificationRejectedException(
                    "verification_policy_unavailable",
                    "The trusted verification profile has no commands.");
            }
            if (candidate.Commands.Any(command =>
                    VerificationBaselineIds.IsReservedCommandId(command.Id)))
            {
                throw new VerificationRejectedException(
                    "verification_policy_unavailable",
                    "The trusted verification profile uses a command id reserved by the built-in baseline.");
            }

            var effectiveRevision = VerificationPolicyCatalogProvider.EffectiveRevision(
                policy.TrustedProfileId,
                key,
                candidate);
            if (!string.Equals(policy.TrustedProfileRevision, effectiveRevision, StringComparison.Ordinal))
            {
                throw new VerificationRejectedException(
                    "verification_policy_unavailable",
                    "The captured trusted verification profile revision is stale.");
            }

            var expectedMandatory = DistinctSortedMandatoryIds(
                candidate.Commands
                    .Where(command => command.Mandatory)
                    .Select(command => command.Id)
                    .Append(IBaselineVerification.RepositoryIntegrityCommandId));
            var capturedMandatory = DistinctSortedMandatoryIds(policy.MandatoryCommandIds);
            if (!expectedMandatory.SequenceEqual(capturedMandatory, StringComparer.Ordinal))
            {
                throw new VerificationRejectedException(
                    "verification_policy_unavailable",
                    "The captured mandatory verification commands do not match the trusted profile.");
            }

            return candidate;
        }

        throw new VerificationRejectedException(
            "verification_policy_unavailable",
            "The trusted verification profile is unavailable on this node.");
    }

    private static string EffectiveProfileId(VerificationProfileOptions profile, string fallback) =>
        string.IsNullOrWhiteSpace(profile.Id) ? fallback : profile.Id;

    private static string[] DistinctSortedMandatoryIds(IEnumerable<string> commandIds) =>
        commandIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    private BaselineVerificationContext CreateBaselineContext(
        RequestVerificationContext context,
        RequestVerificationPolicy policy,
        string? fingerprint = null,
        VerificationRunKind runKind = VerificationRunKind.Baseline) => new(
        context.RequestId,
        context.WorkspaceBindingId!.Value,
        context.BindingValidationRevision!.Value,
        context.RepositoryRoot,
        context.BaselineCommit,
        context.BaselineBranch,
        policy.Revision,
        fingerprint is null
            ? null
            : BindCommandStarting(context, policy, fingerprint, runKind));

    private Func<VerificationCommandStarting, CancellationToken, Task> BindCommandStarting(
        RequestVerificationContext context,
        RequestVerificationPolicy policy,
        string fingerprint,
        VerificationRunKind runKind) =>
        (starting, cancellationToken) =>
        {
            var now = _timeProvider.GetUtcNow();
            return context.EmitAsync(
                "verification.command.started",
                new Dictionary<string, object?>
                {
                    ["fingerprint"] = fingerprint,
                    ["policyRevision"] = policy.Revision,
                    ["commandId"] = starting.CommandId,
                    ["runKind"] = runKind.ToString(),
                    ["mandatory"] = starting.Mandatory,
                    ["startedAt"] = now,
                    ["eventTime"] = now,
                    ["timeoutSeconds"] = starting.TimeoutSeconds,
                },
                cancellationToken);
        };

    private RequestVerificationDecision? FindFinalResult(
        RequestVerificationContext context,
        RequestVerificationPolicy policy,
        string fingerprint)
    {
        var key = new FinalResultKey(context.RequestId, fingerprint, policy.Revision);
        if (_finalResults.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var runs = context.ExistingRuns.Concat(GetRecordedRuns(context.RequestId))
            .Where(run => run.RunKind is VerificationRunKind.Baseline or VerificationRunKind.ProjectCheck
                && string.Equals(run.Fingerprint, fingerprint, StringComparison.Ordinal)
                && string.Equals(run.PolicyRevision, policy.Revision, StringComparison.Ordinal))
            .ToArray();
        if (policy.MandatoryCommandIds.All(commandId => runs.Any(run =>
                string.Equals(run.CommandId, commandId, StringComparison.Ordinal)
                && run.Status == VerificationRunStatus.Passed)))
        {
            var passed = CreateDecision(
                RequestVerificationDecisionKind.Passed,
                "Final verification passed.",
                fingerprint,
                policy.Revision);
            _finalResults[key] = passed;
            return passed;
        }

        if (policy.MandatoryCommandIds.Any(commandId => runs.Any(run =>
                string.Equals(run.CommandId, commandId, StringComparison.Ordinal)
                && run.Status is VerificationRunStatus.Failed
                    or VerificationRunStatus.TimedOut
                    or VerificationRunStatus.Cancelled)))
        {
            var failed = CreateDecision(
                RequestVerificationDecisionKind.Failed,
                "Final verification already failed for the unchanged repository fingerprint.",
                fingerprint,
                policy.Revision,
                "verification_failed");
            _finalResults[key] = failed;
            return failed;
        }

        return null;
    }

    private IReadOnlyList<VerificationRunDto> GetRecordedRuns(Guid requestId) =>
        _recordedRuns.TryGetValue(requestId, out var runs) ? runs : [];

    private async Task PersistAsync(
        RequestVerificationContext context,
        RequestVerificationPolicy policy,
        string fingerprint,
        Guid attemptId,
        string profileId,
        VerificationRunKind runKind,
        IReadOnlyList<VerificationCommandResult> commands,
        CancellationToken cancellationToken)
    {
        foreach (var command in commands)
        {
            var completedAt = _timeProvider.GetUtcNow();
            var run = new VerificationRunDto(
                Guid.Empty,
                context.RequestId,
                profileId,
                command.CommandId,
                ToStatus(command),
                command.ExitCode,
                completedAt - command.Duration,
                completedAt,
                SummarizeCommandOutput(command),
                command.ArtifactPath,
                command.Mandatory,
                fingerprint,
                policy.Revision,
                runKind,
                attemptId);
            await context.PersistRunAsync(run, cancellationToken).ConfigureAwait(false);
            var recorded = _recordedRuns.GetOrAdd(context.RequestId, static _ => []);
            recorded.Add(run);
        }
    }

    private async Task RejectActiveSourceMutationAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var leases = await _reservations.ListAsync(projectId, includeReleased: false, cancellationToken)
            .ConfigureAwait(false);
        if (leases.Any(lease =>
                string.Equals(lease.State, "Active", StringComparison.OrdinalIgnoreCase)
                && lease.Scopes.Any(scope => scope.Kind.Equals("file", StringComparison.OrdinalIgnoreCase)
                    || scope.Kind.Equals("directory", StringComparison.OrdinalIgnoreCase))))
        {
            throw new VerificationRejectedException(
                "active_source_mutation",
                "Verification requires all source-mutation reservations to be released.");
        }
    }

    private static VerificationRunStatus ToStatus(VerificationCommandResult command)
    {
        if (command.TimedOut)
        {
            return VerificationRunStatus.TimedOut;
        }
        if (command.Cancelled)
        {
            return VerificationRunStatus.Cancelled;
        }
        return !command.Crashed && command.ExitCode == 0
            ? VerificationRunStatus.Passed
            : VerificationRunStatus.Failed;
    }

    private static string SummarizeCommandOutput(VerificationCommandResult command)
    {
        var outputLength = Math.Min(command.StandardOutput.Length, MaxSummaryLength + 1);
        var errorLength = Math.Min(
            command.StandardError.Length,
            MaxSummaryLength + 1 - outputLength);
        var summary = string.Concat(
            command.StandardOutput.AsSpan(0, outputLength),
            command.StandardError.AsSpan(0, errorLength));
        return BoundSummary(summary);
    }

    private static RequestVerificationDecision CreateDecision(
        RequestVerificationDecisionKind kind,
        string summary,
        string? fingerprint = null,
        string? policyRevision = null,
        string? errorCode = null) =>
        new(kind, BoundMessage(summary), fingerprint, policyRevision, errorCode);

    private static string BoundSummary(string? summary)
    {
        if (string.IsNullOrEmpty(summary))
        {
            return string.Empty;
        }

        var wasTruncated = summary.Length > MaxSummaryLength;
        var bounded = wasTruncated ? summary[..(MaxSummaryLength + 1)] : summary;
        var sanitized = DiagnosticSanitizer.Sanitize(bounded, MaxSummaryLength);
        if (!wasTruncated && sanitized.Length <= MaxSummaryLength)
        {
            return sanitized;
        }

        return sanitized.Length >= MaxSummaryLength
            ? sanitized[..(MaxSummaryLength - 1)] + "…"
            : sanitized + "…";
    }

    private static string BoundMessage(string? message)
    {
        var summary = BoundSummary(message);
        return string.IsNullOrWhiteSpace(summary)
            ? "Verification failed without additional diagnostic information."
            : summary;
    }

    private static Dictionary<string, object?> EventPayload(RequestVerificationDecision decision) => new()
    {
        ["decision"] = decision.Kind.ToString(),
        ["fingerprint"] = decision.Fingerprint,
        ["policyRevision"] = decision.PolicyRevision,
        ["summary"] = decision.Summary,
        ["errorCode"] = decision.ErrorCode,
    };

    private static Task EmitIntermediateAsync(
        RequestVerificationContext context,
        RequestVerificationDecision decision,
        CancellationToken cancellationToken) =>
        context.EmitAsync("verification.intermediate", EventPayload(decision), cancellationToken);

    private static async Task<RequestVerificationDecision> FailUnavailablePolicyAsync(
        RequestVerificationContext context,
        RequestVerificationPolicy policy,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        var decision = CreateDecision(
            RequestVerificationDecisionKind.Failed,
            message,
            policyRevision: policy.Revision,
            errorCode: code);
        await context.EmitAsync(
            "verification.failed",
            EventPayload(decision),
            cancellationToken).ConfigureAwait(false);
        return decision;
    }

    private static async Task<RequestVerificationDecision> RejectAsync(
        RequestVerificationContext context,
        bool intermediate,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        var decision = CreateDecision(
            RequestVerificationDecisionKind.Rejected,
            message,
            errorCode: code);
        await context.EmitAsync(
            intermediate ? "verification.intermediate" : "verification.rejected",
            EventPayload(decision),
            cancellationToken).ConfigureAwait(false);
        return decision;
    }

    private readonly record struct FinalResultKey(
        Guid RequestId,
        string Fingerprint,
        string PolicyRevision);
}
