using System.Collections.Concurrent;
using System.Text.Json;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Node.Runtime;

namespace PiCommandCenter.Node.Child;

/// <summary>Stable child status values reported by <c>agent.status</c>.</summary>
public static class ChildAgentStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Child-agent supervisor for one node: implements the SPEC §13.3 orchestration tools that the
/// plain root handler gates (<c>agent.spawn</c>/<c>spawn_agents</c>, <c>agent.status</c>,
/// <c>agent.await</c>, <c>agent.cancel</c>), the Agent Mail tools, the reservation lifecycle
/// tools, and the reservation-authorized filesystem tools. Spawn enforces the root→child
/// hierarchy (only root sessions may spawn), the maximum running children per request, the
/// role and runtime-profile allowlists, and unique agent names per request; every child gets a
/// parent link, <c>child.requested</c>/<c>child.started</c> events on the parent's durable
/// event stream, and a terminal <c>child.completed</c>/<c>child.failed</c>/
/// <c>child.cancelled</c> event. Child worker events are appended to the node spool directly.
/// No unrestricted shell or write tools exist: children mutate the repository only through
/// <see cref="ReservedFileOperations"/> after the reservation authority authorizes the lease
/// and fencing token.
/// </summary>
public sealed class PiChildSessionSupervisor : IPiOrchestrationRequestHandler, IAsyncDisposable
{
    private readonly PiWorkerOptions _workerOptions;
    private readonly Lazy<IAgentRuntimeRegistry> _runtimes;
    private readonly IPiOrchestrationRequestHandler _inner;
    private readonly INodeReservationGateway _reservations;
    private readonly INodeMailGateway _mail;
    private readonly INodeEventSpool _spool;
    private readonly ReservedFileOperations _fileOperations;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PiChildSessionSupervisor> _logger;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _disposed;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ChildAgent>>
        _childrenByRequest = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ChildAgent> _childrenBySession = new(StringComparer.Ordinal);

    public PiChildSessionSupervisor(
        IOptions<PiWorkerOptions> workerOptions,
        IPiOrchestrationRequestHandler inner,
        INodeReservationGateway reservations,
        INodeMailGateway mail,
        INodeEventSpool spool,
        TimeProvider timeProvider,
        ILogger<PiChildSessionSupervisor> logger,
        Lazy<IAgentRuntimeRegistry> runtimes)
    {
        ArgumentNullException.ThrowIfNull(workerOptions);
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runtimes);
        _workerOptions = workerOptions.Value;
        _inner = inner;
        _reservations = reservations;
        _mail = mail;
        _spool = spool;
        _fileOperations = new ReservedFileOperations(reservations);
        _timeProvider = timeProvider;
        _logger = logger;
        _runtimes = runtimes;
    }

    /// <summary>Non-terminal child count across all requests, for diagnostics.</summary>
    public int LiveChildCount => _childrenBySession.Values.Count(c => !c.IsTerminal);

    public async Task<PiToolResponse> HandleAsync(
        PiOrchestrationContext context,
        string requestType,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        try
        {
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
                "reservation.handoff.request" or "request_reservation_handoff" => await TransferAsync(context, payload, cancellationToken),
                "reserved_read" => await ReservedReadAsync(context, payload, cancellationToken),
                "reserved_write" => await RunSinglePathMutationAsync(
                    context, payload,
                    (ops, root, lease, sessionId, path, cancellationToken) => ops.WriteTextAsync(
                        root, lease, sessionId, path, payload.GetStringProperty("content") ?? string.Empty, cancellationToken),
                    cancellationToken),
                "reserved_edit" => await RunSinglePathMutationAsync(
                    context, payload,
                    (ops, root, lease, sessionId, path, cancellationToken) => ops.EditTextAsync(
                        root, lease, sessionId, path,
                        payload.GetStringProperty("oldText") ?? string.Empty,
                        payload.GetStringProperty("newText") ?? string.Empty,
                        cancellationToken),
                    cancellationToken),
                "reserved_delete" => await RunSinglePathMutationAsync(
                    context, payload,
                    (ops, root, lease, sessionId, path, cancellationToken) => ops.DeleteAsync(root, lease, sessionId, path, cancellationToken),
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
        if (!_workerOptions.AllowedChildRoles.Contains(spec.Role))
        {
            return SpawnFailure("role_not_allowed", $"Role '{spec.Role}' is not in the role allowlist.");
        }

        if (!_workerOptions.AllowedRuntimeProfiles.Contains(spec.RuntimeProfile))
        {
            return SpawnFailure(
                "runtime_profile_not_allowed",
                $"Runtime profile '{spec.RuntimeProfile}' is not in the profile allowlist.");
        }

        var sessionId = $"pi-child-{context.RequestId:N}-{Guid.NewGuid():N}";
        var child = new ChildAgent(
            sessionId,
            spec.AgentName,
            spec.Role,
            spec.RuntimeProfile,
            context.SessionId,
            context.RequestId,
            context.ProjectId,
            context.NodeId,
            context.RepositoryRoot!,
            _timeProvider.GetUtcNow());
        if (!children.TryAdd(spec.AgentName, child))
        {
            return SpawnFailure(
                "duplicate_agent_name",
                $"An agent named '{spec.AgentName}' already exists for this request.");
        }

        if (!_childrenBySession.TryAdd(sessionId, child))
        {
            children.TryRemove(spec.AgentName, out _);
            return SpawnFailure("duplicate_agent_name", "Child session id collision; retry the spawn.");
        }

        var requestedScopes = spec.RequestedWriteScopes
            .Select(s => new ReservationScopeSpec(s.Kind, s.Path))
            .ToArray();

        await EmitOnParentAsync(
            context,
            "child.requested",
            new Dictionary<string, object?>
            {
                ["childSessionId"] = sessionId,
                ["agentName"] = spec.AgentName,
                ["role"] = spec.Role,
                ["runtimeProfile"] = spec.RuntimeProfile,
                ["parentSessionId"] = context.SessionId,
                ["requestedWriteScopes"] = spec.RequestedWriteScopes,
            },
            cancellationToken).ConfigureAwait(false);

        // Reserve the requested write scopes for the child before it starts; a denial is
        // surfaced to the caller but does not block a read-only child.
        ReservationOperationResult? leaseResult = null;
        if (requestedScopes.Length > 0)
        {
            leaseResult = await _reservations.AcquireAsync(
                Guid.Parse(context.ProjectId),
                Guid.Parse(context.RequestId),
                sessionId,
                requestedScopes,
                $"Write scopes requested by spawn of '{spec.AgentName}'.",
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

        IAgentRuntimeAdapter adapter;
        try
        {
            adapter = _runtimes.Value.Resolve(spec.RuntimeProfile);
        }
        catch (Exception ex)
        {
            children.TryRemove(spec.AgentName, out _);
            _childrenBySession.TryRemove(sessionId, out _);
            return SpawnFailure("runtime_profile_not_allowed", ex.Message);
        }

        if (spec.RuntimeProfile == AgentRuntimeProfiles.ClaudeReservedWrite
            && leaseResult?.Lease is null)
        {
            children.TryRemove(spec.AgentName, out _);
            _childrenBySession.TryRemove(sessionId, out _);
            await AppendChildEventAsync(
                child,
                "child.failed",
                new Dictionary<string, object?>
                {
                    ["agentName"] = spec.AgentName,
                    ["parentSessionId"] = context.SessionId,
                    ["status"] = ChildAgentStatus.Failed,
                    ["reason"] = "Claude reserved-write requires an acquired reservation lease.",
                },
                CancellationToken.None).ConfigureAwait(false);
            return SpawnFailure(
                "reservation_required",
                "Claude reserved-write start requires an acquired lease and fencing token.");
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
                spec.AgentName,
                spec.Role,
                context.RepositoryRoot!,
                spec.Prompt,
                AgentRuntimeMode.Child,
                spec.RuntimeProfile,
                authorization);
            handle = await adapter.StartAsync(start, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            children.TryRemove(spec.AgentName, out _);
            _childrenBySession.TryRemove(sessionId, out _);
            await AppendChildEventAsync(
                child,
                "child.failed",
                new Dictionary<string, object?>
                {
                    ["agentName"] = spec.AgentName,
                    ["parentSessionId"] = context.SessionId,
                    ["status"] = ChildAgentStatus.Failed,
                    ["reason"] = ex.Message,
                },
                CancellationToken.None).ConfigureAwait(false);
            return SpawnFailure("child_start_failed", $"Child '{spec.AgentName}' failed to start: {ex.Message}");
        }

        child.Adapter = adapter;
        child.MarkStarted();
        await EmitOnParentAsync(
            context,
            "child.started",
            new Dictionary<string, object?>
            {
                ["childSessionId"] = sessionId,
                ["providerSessionId"] = handle.ProviderSessionId,
                ["agentName"] = spec.AgentName,
                ["role"] = spec.Role,
                ["runtimeProfile"] = spec.RuntimeProfile,
                ["parentSessionId"] = context.SessionId,
                ["leaseId"] = leaseResult?.Lease?.LeaseId,
            },
            cancellationToken).ConfigureAwait(false);

        _ = WatchChildAsync(child);

        return new Dictionary<string, object?>
        {
            ["agentName"] = spec.AgentName,
            ["childSessionId"] = sessionId,
            ["parentSessionId"] = context.SessionId,
            ["role"] = spec.Role,
            ["runtimeProfile"] = spec.RuntimeProfile,
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

    private async Task<PiToolResponse> TransferAsync(
        PiOrchestrationContext context,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        var lease = ParseLease(payload, out var error);
        var toSessionId = error is null ? payload.GetStringProperty("toSessionId") : null;
        if (error is null && toSessionId is null)
        {
            error = new GatewayError("to_session_required", "toSessionId must be a non-empty string.");
        }

        if (error is not null)
        {
            return PiToolResponse.Failure(error.Code, error.Message);
        }

        var resolvedLease = lease!;


        var result = await _reservations.TransferAsync(
            resolvedLease.LeaseId, context.SessionId, toSessionId!, cancellationToken)
            .ConfigureAwait(false);
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
        Func<ReservedFileOperations, string, MutationLease, string, string, CancellationToken, Task<FileOperationResult>> run,
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
        var result = await run(ops, root, resolvedLease, context.SessionId, path!, cancellationToken)
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


        var (root, ops) = RequireRepository(context);
        var result = await ops.MoveAsync(
            root, resolvedLease, context.SessionId, source!, destination!, cancellationToken)
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

                if (sessionEvent.Type is "session.closed" or "session.failed")
                {
                    if (child.IsTerminal)
                    {
                        // Terminal already emitted (e.g. by an explicit cancel).
                        return;
                    }

                    var failed = sessionEvent.Type == "session.failed";
                    var status = failed
                        ? ChildAgentStatus.Failed
                        : child.CancelRequested ? ChildAgentStatus.Cancelled : ChildAgentStatus.Completed;
                    var reason = sessionEvent.Payload.TryGetValue("reason", out var reasonValue)
                        ? reasonValue?.ToString() ?? "unknown"
                        : "worker_closed";
                    await child.CloseAsync().ConfigureAwait(false);
                    await EmitTerminalAsync(child, status, reason).ConfigureAwait(false);
                    child.Terminal.TrySetResult(new ChildTerminal(status, reason));
                    return;
                }
            }

            // The event stream completed without an explicit close: treat as a crash.
            await child.CloseAsync().ConfigureAwait(false);
            await EmitTerminalAsync(child, ChildAgentStatus.Failed, "worker_stream_ended")
                .ConfigureAwait(false);
            child.Terminal.TrySetResult(new ChildTerminal(ChildAgentStatus.Failed, "worker_stream_ended"));
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            child.Terminal.TrySetResult(new ChildTerminal(ChildAgentStatus.Cancelled, "node_shutdown"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watch loop for child {ChildSessionId} failed.", child.SessionId);
            await child.CloseAsync().ConfigureAwait(false);
            await EmitTerminalAsync(child, ChildAgentStatus.Failed, ex.Message).ConfigureAwait(false);
            child.Terminal.TrySetResult(new ChildTerminal(ChildAgentStatus.Failed, ex.Message));
        }
    }

    private async Task EmitTerminalAsync(ChildAgent child, string status, string reason)
    {
        var type = status switch
        {
            ChildAgentStatus.Completed => "child.completed",
            ChildAgentStatus.Failed => "child.failed",
            _ => "child.cancelled",
        };

        await AppendChildEventAsync(
            child,
            type,
            new Dictionary<string, object?>
            {
                ["agentName"] = child.AgentName,
                ["role"] = child.Role,
                ["runtimeProfile"] = child.RuntimeProfile,
                ["parentSessionId"] = child.ParentSessionId,
                ["status"] = status,
                ["reason"] = reason,
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Appends one event to the child's own durable spool stream.</summary>
    private async Task AppendChildEventAsync(
        ChildAgent child,
        string type,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var sequence = child.AllocateSequence();
        var message = new NodeEventMessage(
            EventId: $"{child.SessionId}-{sequence}-{type}",
            NodeId: Guid.Parse(child.NodeId),
            ProjectId: Guid.Parse(child.ProjectId),
            RequestId: Guid.Parse(child.RequestId),
            SessionId: child.SessionId,
            Sequence: sequence,
            Type: type,
            OccurredAt: _timeProvider.GetUtcNow(),
            PayloadJson: JsonSerializer.Serialize(payload, JsonOptions));

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
            return _childrenBySession.TryGetValue(childSessionId, out var bySession) ? [bySession] : [];
        }

        return [.. children.Values];
    }

    private ChildAgent? SelectChild(PiOrchestrationContext context, JsonElement? payload)
    {
        var selected = SelectChildren(context, payload);
        return selected.Count == 1 ? selected[0] : null;
    }

    private static Dictionary<string, object?> ToStatusView(ChildAgent child) => new()
    {
        ["agentName"] = child.AgentName,
        ["childSessionId"] = child.SessionId,
        ["role"] = child.Role,
        ["runtimeProfile"] = child.RuntimeProfile,
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
        foreach (var field in new[] { "agentName", "role", "runtimeProfile", "prompt" })
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
            element.GetStringProperty("runtimeProfile")!,
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
        }

        _disposeCts.Dispose();
    }
}

/// <summary>One validated child spawn request from the worker payload.</summary>
public sealed record SpawnSpec(
    string AgentName,
    string Role,
    string RuntimeProfile,
    string Prompt,
    IReadOnlyList<ReservationScopeSpec> RequestedWriteScopes);
