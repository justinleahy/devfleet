using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.RuntimeRouting;
using PiCommandCenter.Node.SystemResources;
using PiCommandCenter.Node.Recovery;


namespace PiCommandCenter.Node;

/// <summary>
/// The subset of the Control Plane transport the worker loop needs. Implemented by
/// <see cref="NodeTransportClient"/>; faked in tests.
/// </summary>
public interface INodeHubOps : IAsyncDisposable
{
    /// <summary>Raised after the hub is connected and the node is registered.</summary>
    event Func<Task>? Connected;

    /// <summary>Raised when the Control Plane commands this node to cancel a session.</summary>
    event Func<CancelSessionCommand, Task>? CancelSessionReceived;

    /// <summary>Raised when the Control Plane cancels a durable assignment owned by this node.</summary>
    event Func<CancelAssignmentCommand, Task>? CancelAssignmentReceived;

    /// <summary>
    /// Raised when the Control Plane commands this node to recover one assignment.
    /// Recovery always stops; it never resumes interrupted execution.
    /// </summary>
    event Func<RecoverAssignmentCommandMessage, Task>? RecoverAssignmentReceived;


    HubConnectionState State { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task HeartbeatAsync(
        IReadOnlyList<string> activeSessionIds,
        NodeResourceSnapshotMessage resources,
        NodeExecutionStatusMessage executionStatus,
        CancellationToken cancellationToken);

    Task<ExecutionAssignmentMessage?> ClaimNextAsync(int leaseSeconds, CancellationToken cancellationToken);

    /// <summary>Returns the new lease expiry, or null when the assignment was lost.</summary>
    Task<DateTimeOffset?> RenewAssignmentAsync(
        ExecutionAssignmentMessage assignment,
        CancellationToken cancellationToken);
    Task<ReconcileAssignmentsResultMessage> ReconcileAssignmentsAsync(
        IReadOnlyList<ExecutionAssignmentInventoryItemMessage> assignments,
        CancellationToken cancellationToken);

    Task<NodeEventAcknowledgementMessage> PublishEventsAsync(
        IReadOnlyList<NodeEventMessage> events,
        CancellationToken cancellationToken);

    Task ReportRecoveryProgressAsync(
        AssignmentRecoveryProgressMessage message,
        CancellationToken cancellationToken);

    Task ReportRecoveryProofAsync(
        AssignmentRecoveryProofMessage message,
        CancellationToken cancellationToken);
}

/// <summary>Starts and tracks root Pi sessions for execution assignments.</summary>
public interface IRootSessionSupervisor
{
    IReadOnlyList<string> ActiveSessionIds { get; }

    Task<string> StartForAssignmentAsync(
        ExecutionAssignmentMessage assignment,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? preparationCompleted = null);

    Task<bool> CancelSessionAsync(string sessionId, string reason);
    Guid? FindRequestId(string sessionId);
}


/// <summary>
/// Cancels one locally running child session by id and reports all active sessions for heartbeats.
/// Root cancellation is handled by <see cref="IRootSessionTerminalizer"/>.
/// </summary>
public interface ISessionCanceller
{
    Task<bool> CancelChildSessionAsync(string sessionId, string reason);

    IReadOnlyList<string> ActiveSessionIds { get; }
}

/// <summary>
/// Node background service: connects outbound to the Control Plane, registers on
/// every connection, replays locally spooled unacknowledged events, heartbeats, and
/// holds up to <see cref="NodeOptions.MaxConcurrentRequests"/> concurrent assignments.
/// Assignments are renewed before expiry and reconciled periodically (never dropped silently) so
/// locally running sessions keep running across Control Plane restarts.
/// </summary>
public sealed class NodeWorker
    : BackgroundService, INodeAssignmentTerminalizationOrchestrator
{
    internal const int ReplayBatchSize = 100;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);

    private readonly NodeOptions _options;
    private readonly INodeHubOps _transport;
    private readonly INodeEventSpool _spool;
    private readonly INodeAssignmentJournal _journal;
    private readonly ISessionCanceller _sessionCanceller;
    private readonly IRootSessionSupervisor _rootSessions;
    private readonly IRootSessionTerminalizer _rootTerminalizer;
    private readonly TimeProvider _timeProvider;
    private readonly INodeSystemResourceMonitor _resourceMonitor;
    private readonly IRuntimeReadinessProvider _readinessProvider;
    private readonly NodeAssignmentCredentialSource _assignmentCredentials;
    private readonly IAssignmentRecoveryRunner _recoveryRunner;
    private readonly HashSet<Guid> _recoveryStoppedRequests = [];
    private readonly ILogger<NodeWorker> _logger;
    private readonly object _assignmentsLock = new();
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly Dictionary<Guid, NodeAssignmentJournalEntry> _activeAssignments = new();
    private readonly Dictionary<Guid, PendingAssignmentCancellation> _pendingAssignmentCancellations = new();
    private readonly CancellationTokenSource _cancellationShutdown = new();
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReconciliation = DateTimeOffset.MinValue;
    private DateTimeOffset? _nextAssignmentEligibleAt;
    private bool _journalLoaded;
    private bool _dispatchEnabled = true;
    private int _pendingCancellations;

    public NodeWorker(
        IOptions<NodeOptions> options,
        INodeHubOps transport,
        INodeEventSpool spool,
        INodeAssignmentJournal journal,
        TimeProvider timeProvider,
        INodeSystemResourceMonitor resourceMonitor,
        IRuntimeReadinessProvider readinessProvider,
        ISessionCanceller sessionCanceller,
        IRootSessionTerminalizer rootTerminalizer,
        NodeAssignmentCredentialSource assignmentCredentials,
        IRootSessionSupervisor rootSessions,
        IAssignmentRecoveryRunner recoveryRunner,
        ILogger<NodeWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(resourceMonitor);
        ArgumentNullException.ThrowIfNull(readinessProvider);
        ArgumentNullException.ThrowIfNull(assignmentCredentials);
        ArgumentNullException.ThrowIfNull(sessionCanceller);
        ArgumentNullException.ThrowIfNull(rootTerminalizer);
        ArgumentNullException.ThrowIfNull(rootSessions);
        ArgumentNullException.ThrowIfNull(recoveryRunner);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _transport = transport;
        _spool = spool;
        _journal = journal;
        _timeProvider = timeProvider;
        _resourceMonitor = resourceMonitor;
        _readinessProvider = readinessProvider;
        _assignmentCredentials = assignmentCredentials;
        _sessionCanceller = sessionCanceller;
        _rootTerminalizer = rootTerminalizer;
        _rootSessions = rootSessions;
        _recoveryRunner = recoveryRunner;
        _logger = logger;

        _transport.Connected += HandleConnectedAsync;
        _transport.CancelSessionReceived += OnCancelSessionReceivedAsync;
        _transport.CancelAssignmentReceived += OnCancelAssignmentReceivedAsync;
        _transport.RecoverAssignmentReceived += OnRecoverAssignmentReceivedAsync;
    }

    private async Task OnCancelSessionReceivedAsync(CancelSessionCommand command)
    {
        Interlocked.Increment(ref _pendingCancellations);
        try
        {
            await _dispatchGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _dispatchGate.Release();

            _logger.LogInformation(
                "Control Plane requested cancellation of session {SessionId}: {Reason}",
                command.SessionId,
                command.Reason);

            var rootRequestId = _rootSessions.FindRequestId(command.SessionId);
            if (rootRequestId is not { } requestId)
            {
                var childStopped = await _sessionCanceller
                    .CancelChildSessionAsync(command.SessionId, command.Reason)
                    .ConfigureAwait(false);
                if (!childStopped)
                {
                    _logger.LogWarning(
                        "Child session {SessionId} could not be cancelled locally; it may have already stopped.",
                        command.SessionId);
                }

                return;
            }

            ExecutionAssignmentMessage? assignment;
            lock (_assignmentsLock)
            {
                assignment = _activeAssignments.TryGetValue(requestId, out var entry)
                    ? entry.Assignment
                    : null;
            }

            if (assignment is null)
            {
                _logger.LogWarning(
                    "Root session {SessionId} has no active assignment; cancellation was not started.",
                    command.SessionId);
                return;
            }

            var outcome = await _rootTerminalizer
                .CancelAsync(assignment, command.SessionId, command.Reason, CancellationToken.None)
                .ConfigureAwait(false);
            if (outcome == RootTerminalizationOutcome.Accepted)
            {
                await RemoveAssignmentAsync(requestId, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            _logger.LogWarning(
                "Root cancellation for request {RequestId} was {Outcome}; assignment ownership was retained.",
                requestId,
                outcome);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCancellations);
        }
    }

    private async Task OnCancelAssignmentReceivedAsync(CancelAssignmentCommand command)
    {
        Interlocked.Increment(ref _pendingCancellations);
        try
        {
            await _dispatchGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await RegisterPendingAssignmentCancellationAsync(
                    command.RequestId,
                    rootSessionId: null,
                    command.Reason,
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _dispatchGate.Release();
            }

            var attempt = StartPendingAssignmentCancellation(command.RequestId);
            if (attempt is not null)
            {
                await attempt.ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingCancellations);
        }
    }

    private async Task OnRecoverAssignmentReceivedAsync(RecoverAssignmentCommandMessage command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_assignmentsLock)
        {
            _recoveryStoppedRequests.Add(command.RequestId);
        }

        try
        {
            var proof = await _recoveryRunner
                .RunAsync(command, ReportRecoveryProgressSafeAsync, _cancellationShutdown.Token)
                .ConfigureAwait(false);
            await ReportRecoveryProofSafeAsync(proof, _cancellationShutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellationShutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "RecoverAssignment {RecoveryId} attempt {Attempt} for request {RequestId} failed.",
                command.RecoveryId,
                command.Attempt,
                command.RequestId);
        }
    }

    private async Task ReportRecoveryProgressSafeAsync(
        AssignmentRecoveryProgressMessage progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _transport.ReportRecoveryProgressAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                ex,
                "Recovery progress for {RecoveryId} attempt {Attempt} was not delivered.",
                progress.RecoveryId,
                progress.Attempt);
        }
    }

    private async Task ReportRecoveryProofSafeAsync(
        AssignmentRecoveryProofMessage proof,
        CancellationToken cancellationToken)
    {
        try
        {
            await _transport.ReportRecoveryProofAsync(proof, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                ex,
                "Recovery proof for {RecoveryId} attempt {Attempt} was not delivered.",
                proof.RecoveryId,
                proof.Attempt);
        }
    }


    public async Task<CompletionGateDecision> BeginTerminalizationAsync(
        Guid requestId,
        TerminalizationIntent intent,
        Func<CancellationToken, Task<CompletionGateDecision>> beginAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beginAsync);
        var localState = intent switch
        {
            TerminalizationIntent.Complete or TerminalizationIntent.Fail => "Finalizing",
            TerminalizationIntent.Cancel => "Cancelling",
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown terminalization intent."),
        };

        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NodeAssignmentJournalEntry entry;
            lock (_assignmentsLock)
            {
                if (!_activeAssignments.TryGetValue(requestId, out entry!))
                {
                    throw new InvalidOperationException(
                        $"No active assignment exists for request '{requestId}'.");
                }
            }

            var decision = await beginAsync(cancellationToken).ConfigureAwait(false);
            if (!decision.Accepted || entry.Assignment.State == localState)
            {
                return decision;
            }

            var terminalizing = entry with
            {
                Assignment = entry.Assignment with { State = localState },
            };
            await _journal.UpsertAsync(terminalizing, CancellationToken.None).ConfigureAwait(false);
            ReplaceAssignment(terminalizing);
            return decision;
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await LoadJournalAsync(stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _transport.StartAsync(stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Node {NodeId} connected to control plane {ControlPlaneUrl}.",
                        _options.Id,
                        _options.ControlPlaneUrl);

                    await RunConnectedLoopAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Node loop failed; retrying in {Backoff}.", ReconnectBackoff);
                }

                await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await SafeDelayAsync(ReconnectBackoff, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _transport.Connected -= HandleConnectedAsync;
            _transport.CancelSessionReceived -= OnCancelSessionReceivedAsync;
            _transport.CancelAssignmentReceived -= OnCancelAssignmentReceivedAsync;
            _transport.RecoverAssignmentReceived -= OnRecoverAssignmentReceivedAsync;
            _cancellationShutdown.Cancel();
            await AwaitPendingAssignmentCancellationsAsync().ConfigureAwait(false);
            await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _spool.DisposeAsync().ConfigureAwait(false);
            await _journal.DisposeAsync().ConfigureAwait(false);
            _cancellationShutdown.Dispose();
            _dispatchGate.Dispose();
            _logger.LogInformation("Node {NodeId} stopped.", _options.Id);
        }
    }

    internal async Task LoadJournalAsync(CancellationToken cancellationToken)
    {
        if (_journalLoaded)
        {
            return;
        }

        var entries = await _journal.LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_assignmentsLock)
        {
            foreach (var entry in entries)
            {
                _activeAssignments.Add(entry.Assignment.RequestId, entry);
                _assignmentCredentials.Track(ToCredential(entry.Assignment));
            }

            _journalLoaded = true;
        }
    }

    internal async Task HandleConnectedAsync()
    {
        await _dispatchGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _dispatchEnabled = false;
            await ReconcileAssignmentsAsync(CancellationToken.None).ConfigureAwait(false);
            _lastReconciliation = _timeProvider.GetUtcNow();
            await ReplayPendingEventsAsync(CancellationToken.None).ConfigureAwait(false);
            _dispatchEnabled = true;
        }
        finally
        {
            _dispatchGate.Release();
            StartPendingAssignmentCancellations();
        }
    }

    internal async Task RunConnectedLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested
               && _transport.State == HubConnectionState.Connected)
        {
            await RunTickAsync(_timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            await SafeDelayAsync(TickInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One deterministic unit of worker work at a point in time.</summary>
    internal async Task RunTickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RenewDueAssignmentsAsync(now, cancellationToken).ConfigureAwait(false);
            await ReplayPendingEventsAsync(cancellationToken).ConfigureAwait(false);
            await ReconcileAssignmentsIfDueAsync(now, cancellationToken).ConfigureAwait(false);
            await RetryStartBlockedAssignmentsAsync(cancellationToken).ConfigureAwait(false);
            await StartNextAssignmentIfCapacityAsync(cancellationToken).ConfigureAwait(false);

            if (now - _lastHeartbeat >= TimeSpan.FromSeconds(_options.HeartbeatSeconds))
            {
                var resources = _resourceMonitor.Capture();
                var activeAssignmentIds = ActiveAssignmentRequestIdsSnapshot();
                var executionStatus = _readinessProvider.Capture(activeAssignmentIds);
                await _transport
                    .HeartbeatAsync(
                        _sessionCanceller.ActiveSessionIds,
                        resources,
                        executionStatus,
                        cancellationToken)
                    .ConfigureAwait(false);
                _lastHeartbeat = now;
            }
        }
        finally
        {
            _dispatchGate.Release();
            StartPendingAssignmentCancellations();
        }
    }

    internal async Task ReplayPendingEventsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested
               && _transport.State == HubConnectionState.Connected)
        {
            var pending = await _spool.PeekPendingAsync(ReplayBatchSize, cancellationToken).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                break;
            }

            _logger.LogInformation("Replaying {Count} pending node event(s).", pending.Count);
            var acknowledgement = await _transport.PublishEventsAsync(pending, cancellationToken)
                .ConfigureAwait(false);
            if (acknowledgement.EventIds.Count > 0)
            {
                await _spool.DeleteAsync(acknowledgement.EventIds, cancellationToken).ConfigureAwait(false);
            }

            if (acknowledgement.EventIds.Count == 0)
            {
                // Nothing was accepted; avoid spinning on the same batch.
                break;
            }
        }
    }

    private async Task RenewDueAssignmentsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        List<NodeAssignmentJournalEntry> due;
        lock (_assignmentsLock)
        {
            var renewalThreshold = TimeSpan.FromSeconds(_options.ClaimLeaseSeconds / 3.0);
            due = _activeAssignments.Values
                .Where(entry => now >= entry.Assignment.LeaseExpiresAt - renewalThreshold)
                .ToList();
        }

        foreach (var entry in due)
        {
            var assignment = entry.Assignment;
            var newExpiry = await _transport
                .RenewAssignmentAsync(assignment, cancellationToken)
                .ConfigureAwait(false);
            if (newExpiry is DateTimeOffset expiry && expiry > now)
            {
                _logger.LogDebug(
                    "Renewed assignment for request {RequestId} until {LeaseExpiresAt}.",
                    assignment.RequestId,
                    expiry);
                var renewed = entry with
                {
                    Assignment = assignment with { LeaseExpiresAt = expiry },
                };
                await _journal.UpsertAsync(renewed, cancellationToken).ConfigureAwait(false);
                ReplaceAssignment(renewed);
            }
            else
            {
                _logger.LogInformation(
                    "Assignment for request {RequestId} was not renewed; ownership was retained.",
                    assignment.RequestId);
            }
        }
    }

    private async Task RetryStartBlockedAssignmentsAsync(CancellationToken cancellationToken)
    {
        NodeAssignmentJournalEntry[] blocked;
        lock (_assignmentsLock)
        {
            blocked =
            [
                .. _activeAssignments.Values
                    .Where(entry => entry.SupervisorState == AssignmentSupervisorState.StartBlocked
                        && entry.Assignment.State != "Cancelling"
                        && !_recoveryStoppedRequests.Contains(entry.Assignment.RequestId)),
            ];
        }

        foreach (var entry in blocked)
        {
            try
            {
                await _rootSessions
                    .StartForAssignmentAsync(
                        entry.Assignment,
                        cancellationToken,
                        preparationCompleted: ct => MarkPreparationCompletedAsync(
                            entry.Assignment.RequestId,
                            ct))
                    .ConfigureAwait(false);
                var running = entry with
                {
                    SupervisorState = AssignmentSupervisorState.Running,
                    RepositoryKnown = true,
                };
                await _journal.UpsertAsync(running, cancellationToken).ConfigureAwait(false);
                ReplaceAssignment(running);
                await AppendStartupUnblockedAsync(entry.Assignment, cancellationToken).ConfigureAwait(false);
                await RefreshPendingEventCountAsync(entry.Assignment.RequestId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                var blockedEntry = entry with
                {
                    SupervisorState = AssignmentSupervisorState.StartBlocked,
                    RepositoryKnown = false,
                };
                await _journal.UpsertAsync(blockedEntry, cancellationToken).ConfigureAwait(false);
                await AppendStartupBlockedAsync(entry.Assignment, ex, cancellationToken).ConfigureAwait(false);
                ReplaceAssignment(blockedEntry);
                await RefreshPendingEventCountAsync(entry.Assignment.RequestId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task StartNextAssignmentIfCapacityAsync(CancellationToken cancellationToken)
    {
        int activeCount;
        lock (_assignmentsLock)
        {
            activeCount = _activeAssignments.Count;
        }

        if (!_dispatchEnabled
            || Volatile.Read(ref _pendingCancellations) != 0
            || activeCount >= _options.MaxConcurrentRequests)
        {
            return;
        }

        if (_nextAssignmentEligibleAt is DateTimeOffset eligibleAt
            && _timeProvider.GetUtcNow() < eligibleAt)
        {
            return;
        }

        var assignment = await _transport
            .ClaimNextAsync(_options.ClaimLeaseSeconds, cancellationToken)
            .ConfigureAwait(false);
        if (assignment is null)
        {
            _nextAssignmentEligibleAt = _timeProvider.GetUtcNow().AddSeconds(1);
            return;
        }

        _nextAssignmentEligibleAt = null;
        if (ContainsAssignment(assignment.RequestId) || IsRecoveryStopped(assignment.RequestId))
        {
            return;
        }

        var initialEntry = new NodeAssignmentJournalEntry(
            assignment,
            AssignmentSupervisorState.StartBlocked,
            RepositoryKnown: false,
            PendingEventCount: await _spool
                .CountPendingForRequestAsync(assignment.RequestId, cancellationToken)
                .ConfigureAwait(false));
        await _journal.UpsertAsync(initialEntry, cancellationToken).ConfigureAwait(false);
        if (!TrackAssignment(initialEntry))
        {
            return;
        }

        try
        {
            await _rootSessions
                .StartForAssignmentAsync(
                    assignment,
                    cancellationToken,
                    preparationCompleted: ct => MarkPreparationCompletedAsync(
                        assignment.RequestId,
                        ct))
                .ConfigureAwait(false);
            var runningEntry = initialEntry with
            {
                SupervisorState = AssignmentSupervisorState.Running,
                RepositoryKnown = true,
                PendingEventCount = await _spool
                    .CountPendingForRequestAsync(assignment.RequestId, cancellationToken)
                    .ConfigureAwait(false),
            };
            await _journal.UpsertAsync(runningEntry, cancellationToken).ConfigureAwait(false);
            ReplaceAssignment(runningEntry);
            _logger.LogInformation(
                "Started assignment for request {RequestId} (project {ProjectId}) until {LeaseExpiresAt}.",
                assignment.RequestId,
                assignment.ProjectId,
                assignment.LeaseExpiresAt);
        }
        catch (InvalidOperationException ex)
        {
            var blockedEntry = initialEntry with
            {
                SupervisorState = AssignmentSupervisorState.StartBlocked,
                RepositoryKnown = false,
            };
            await _journal.UpsertAsync(blockedEntry, cancellationToken).ConfigureAwait(false);
            await AppendStartupBlockedAsync(assignment, ex, cancellationToken).ConfigureAwait(false);
            ReplaceAssignment(blockedEntry);
            await RefreshPendingEventCountAsync(assignment.RequestId, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogWarning(
                "Request {RequestId} was blocked during root startup: {Reason}",
                assignment.RequestId,
                Security.DiagnosticSanitizer.Sanitize(ex.Message, 512));
        }
    }


    private async Task MarkPreparationCompletedAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        NodeAssignmentJournalEntry entry;
        lock (_assignmentsLock)
        {
            if (!_activeAssignments.TryGetValue(requestId, out entry!))
            {
                throw new InvalidOperationException(
                    $"Assignment {requestId} disappeared during workspace preparation.");
            }
        }

        var prepared = entry with
        {
            SupervisorState = AssignmentSupervisorState.Unknown,
            RepositoryKnown = true,
        };
        await _journal.UpsertAsync(prepared, cancellationToken).ConfigureAwait(false);
        ReplaceAssignment(prepared);
    }

    private Task AppendStartupBlockedAsync(
        ExecutionAssignmentMessage assignment,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (!_assignmentCredentials.TryGetByRequest(assignment.RequestId, out var credential)
            || credential.RequestId != assignment.RequestId
            || credential.ProjectId != assignment.ProjectId)
        {
            throw new InvalidOperationException(
                $"No active assignment credential is available for request {assignment.RequestId} in project {assignment.ProjectId}.");
        }

        var eventId = $"assignment-start-{assignment.RequestId:N}-request.blocked";
        var reason = Security.DiagnosticSanitizer.Sanitize(exception.Message, 512);
        return _spool.AppendAsync(
            new NodeEventMessage(
                EventId: eventId,
                NodeId: assignment.NodeIdSnapshot,
                ProjectId: assignment.ProjectId,
                RequestId: assignment.RequestId,
                ClaimToken: credential.ClaimToken,
                SessionId: null,
                Sequence: 1,
                Type: "request.blocked",
                OccurredAt: _timeProvider.GetUtcNow(),
                PayloadJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["status"] = "blocked",
                    ["reason"] = reason,
                    ["phase"] = "root_start",
                })),
            cancellationToken);
    }
    private Task AppendStartupUnblockedAsync(
        ExecutionAssignmentMessage assignment,
        CancellationToken cancellationToken)
    {
        if (!_assignmentCredentials.TryGetByRequest(assignment.RequestId, out var credential))
        {
            throw new InvalidOperationException(
                $"No active assignment credential is available for request {assignment.RequestId}.");
        }

        return _spool.AppendAsync(
            new NodeEventMessage(
                EventId: $"assignment-start-{assignment.RequestId:N}-request.unblocked",
                NodeId: assignment.NodeIdSnapshot,
                ProjectId: assignment.ProjectId,
                RequestId: assignment.RequestId,
                ClaimToken: credential.ClaimToken,
                SessionId: null,
                Sequence: 2,
                Type: "request.unblocked",
                OccurredAt: _timeProvider.GetUtcNow(),
                PayloadJson: """{"status":"starting","phase":"root_start"}"""),
            cancellationToken);
    }


    private async Task ReconcileAssignmentsIfDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (now - _lastReconciliation < TimeSpan.FromSeconds(_options.HeartbeatSeconds))
        {
            return;
        }

        await ReconcileAssignmentsAsync(cancellationToken).ConfigureAwait(false);
        _lastReconciliation = now;
    }

    private async Task ReconcileAssignmentsAsync(CancellationToken cancellationToken)
    {
        NodeAssignmentJournalEntry[] entries;
        lock (_assignmentsLock)
        {
            entries = [.. _activeAssignments.Values];
        }

        var inventory = new List<ExecutionAssignmentInventoryItemMessage>(entries.Length);
        foreach (var entry in entries)
        {
            var current = entry with
            {
                SupervisorState = ObserveSupervisorState(entry),
                PendingEventCount = await _spool
                    .CountPendingForRequestAsync(entry.Assignment.RequestId, cancellationToken)
                    .ConfigureAwait(false),
            };
            await _journal.UpsertAsync(current, cancellationToken).ConfigureAwait(false);
            ReplaceAssignment(current);
            inventory.Add(new ExecutionAssignmentInventoryItemMessage(
                current.Assignment,
                current.SupervisorState,
                current.RepositoryKnown,
                current.PendingEventCount));
        }

        var reconciliation = await _transport
            .ReconcileAssignmentsAsync(inventory, cancellationToken)
            .ConfigureAwait(false);
        foreach (var result in reconciliation.Assignments)
        {
            NodeAssignmentJournalEntry? current;
            lock (_assignmentsLock)
            {
                _activeAssignments.TryGetValue(result.RequestId, out current);
            }

            if (current is null)
            {
                continue;
            }

            switch (result.Disposition)
            {
                case AssignmentReconciliationDisposition.Resume:
                    {
                        var resumed = current with { Assignment = result.Assignment! };
                        await _journal.UpsertAsync(resumed, cancellationToken).ConfigureAwait(false);
                        ReplaceAssignment(resumed);
                        break;
                    }
                case AssignmentReconciliationDisposition.Cancel:
                    if (result.Assignment is not null)
                    {
                        var cancelling = current with { Assignment = result.Assignment };
                        await _journal.UpsertAsync(cancelling, cancellationToken).ConfigureAwait(false);
                        ReplaceAssignment(cancelling);
                        await RegisterPendingAssignmentCancellationAsync(
                            result.RequestId,
                            rootSessionId: null,
                            "control-plane-reconciliation",
                            cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case AssignmentReconciliationDisposition.RecoveryRequired:
                    break;
                case AssignmentReconciliationDisposition.Terminal:
                    await RemoveAssignmentAsync(result.RequestId, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task RegisterPendingAssignmentCancellationAsync(
        Guid requestId,
        string? rootSessionId,
        string reason,
        CancellationToken cancellationToken)
    {
        NodeAssignmentJournalEntry? entry;
        lock (_assignmentsLock)
        {
            _activeAssignments.TryGetValue(requestId, out entry);
        }

        if (entry is null)
        {
            _logger.LogWarning(
                "Cancellation requested for unknown assignment {RequestId}; no local work was released.",
                requestId);
            return;
        }

        var cancelling = entry;
        if (entry.Assignment.State != "Cancelling")
        {
            cancelling = entry with { Assignment = entry.Assignment with { State = "Cancelling" } };
            await _journal.UpsertAsync(cancelling, cancellationToken).ConfigureAwait(false);
            ReplaceAssignment(cancelling);
        }

        rootSessionId ??= _rootSessions.ActiveSessionIds.SingleOrDefault(
            sessionId => _rootSessions.FindRequestId(sessionId) == requestId);

        lock (_assignmentsLock)
        {
            if (_pendingAssignmentCancellations.TryGetValue(requestId, out var pending))
            {
                pending.RootSessionId ??= rootSessionId;
                pending.Reason = reason;
                if (pending.Attempt is { IsCompleted: true })
                {
                    pending.Attempt = null;
                }

                return;
            }

            _pendingAssignmentCancellations.Add(
                requestId,
                new PendingAssignmentCancellation(rootSessionId, reason));
            Interlocked.Increment(ref _pendingCancellations);
        }
    }

    private void StartPendingAssignmentCancellations()
    {
        Guid[] requestIds;
        lock (_assignmentsLock)
        {
            requestIds = [.. _pendingAssignmentCancellations.Keys];
        }

        foreach (var requestId in requestIds)
        {
            StartPendingAssignmentCancellation(requestId);
        }
    }

    private Task? StartPendingAssignmentCancellation(Guid requestId)
    {
        Task attempt;
        lock (_assignmentsLock)
        {
            if (_cancellationShutdown.IsCancellationRequested
                || !_pendingAssignmentCancellations.TryGetValue(requestId, out var pending))
            {
                return null;
            }

            if (pending.Attempt is not null)
            {
                return pending.Attempt;
            }

            var rootSessionId = pending.RootSessionId;
            var reason = pending.Reason;
            attempt = Task.Run(
                () => TerminalizePendingAssignmentCancellationAsync(
                    requestId,
                    pending,
                    rootSessionId,
                    reason,
                    _cancellationShutdown.Token));
            pending.Attempt = attempt;
        }

        _ = ObservePendingAssignmentCancellationAsync(requestId, attempt);
        return attempt;
    }

    private async Task TerminalizePendingAssignmentCancellationAsync(
        Guid requestId,
        PendingAssignmentCancellation pending,
        string? rootSessionId,
        string reason,
        CancellationToken cancellationToken)
    {
        NodeAssignmentJournalEntry? entry;
        bool terminalAccepted;
        lock (_assignmentsLock)
        {
            _activeAssignments.TryGetValue(requestId, out entry);
            terminalAccepted = pending.TerminalAccepted;
        }

        if (entry is null)
        {
            return;
        }

        if (!terminalAccepted)
        {
            var outcome = rootSessionId is null
                ? await _rootTerminalizer
                    .CancelBeforeRootAsync(entry.Assignment, reason, cancellationToken)
                    .ConfigureAwait(false)
                : await _rootTerminalizer
                    .CancelAsync(entry.Assignment, rootSessionId, reason, cancellationToken)
                    .ConfigureAwait(false);
            if (outcome != RootTerminalizationOutcome.Accepted)
            {
                _logger.LogWarning(
                    "Root cancellation for request {RequestId} was {Outcome}; assignment ownership was retained.",
                    requestId,
                    outcome);
                return;
            }

            lock (_assignmentsLock)
            {
                if (!_pendingAssignmentCancellations.TryGetValue(requestId, out var current)
                    || !ReferenceEquals(current, pending))
                {
                    return;
                }

                pending.TerminalAccepted = true;
            }
        }

        await _dispatchGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await RemoveAssignmentAsync(requestId, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _dispatchGate.Release();
        }
    }

    private async Task ObservePendingAssignmentCancellationAsync(Guid requestId, Task attempt)
    {
        try
        {
            await attempt.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellationShutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Root cancellation attempt for request {RequestId} failed; assignment ownership was retained.",
                requestId);
        }
    }

    private async Task AwaitPendingAssignmentCancellationsAsync()
    {
        Task[] attempts;
        lock (_assignmentsLock)
        {
            attempts =
            [
                .. _pendingAssignmentCancellations.Values
                    .Select(pending => pending.Attempt)
                    .OfType<Task>(),
            ];
        }

        foreach (var attempt in attempts)
        {
            try
            {
                await attempt.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Every attempt is observed and logged when it completes.
            }
        }
    }

    private AssignmentSupervisorState ObserveSupervisorState(NodeAssignmentJournalEntry entry)
    {
        if (entry.SupervisorState is AssignmentSupervisorState.Unknown
            or AssignmentSupervisorState.StartBlocked)
        {
            return entry.SupervisorState;
        }

        return _rootSessions.ActiveSessionIds.Any(
            sessionId => _rootSessions.FindRequestId(sessionId) == entry.Assignment.RequestId)
            ? AssignmentSupervisorState.Running
            : AssignmentSupervisorState.Stopped;
    }

    private bool ContainsAssignment(Guid requestId)
    {
        lock (_assignmentsLock)
        {
            return _activeAssignments.ContainsKey(requestId);
        }
    }

    private bool IsRecoveryStopped(Guid requestId)
    {
        lock (_assignmentsLock)
        {
            return _recoveryStoppedRequests.Contains(requestId);
        }
    }


    private bool TrackAssignment(NodeAssignmentJournalEntry entry)
    {
        lock (_assignmentsLock)
        {
            if (_activeAssignments.ContainsKey(entry.Assignment.RequestId))
            {
                return false;
            }

            _assignmentCredentials.Track(ToCredential(entry.Assignment));
            _activeAssignments[entry.Assignment.RequestId] = entry;
            return true;
        }
    }

    private void ReplaceAssignment(NodeAssignmentJournalEntry entry)
    {
        lock (_assignmentsLock)
        {
            _assignmentCredentials.Track(ToCredential(entry.Assignment));
            _activeAssignments[entry.Assignment.RequestId] = entry;
        }
    }

    private async Task RefreshPendingEventCountAsync(
        Guid requestId,
        CancellationToken cancellationToken)
    {
        NodeAssignmentJournalEntry? entry;
        lock (_assignmentsLock)
        {
            _activeAssignments.TryGetValue(requestId, out entry);
        }

        if (entry is null)
        {
            return;
        }

        var updated = entry with
        {
            PendingEventCount = await _spool
                .CountPendingForRequestAsync(requestId, cancellationToken)
                .ConfigureAwait(false),
        };
        await _journal.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        ReplaceAssignment(updated);
    }

    private async Task RemoveAssignmentAsync(Guid requestId, CancellationToken cancellationToken)
    {
        await _journal.DeleteAsync(requestId, cancellationToken).ConfigureAwait(false);
        var pendingCancellationRemoved = false;
        lock (_assignmentsLock)
        {
            if (_activeAssignments.Remove(requestId, out var entry))
            {
                _assignmentCredentials.Remove(ToCredential(entry.Assignment));
            }

            pendingCancellationRemoved = _pendingAssignmentCancellations.Remove(requestId);
        }

        if (pendingCancellationRemoved)
        {
            Interlocked.Decrement(ref _pendingCancellations);
        }
    }

    private static NodeAssignmentCredential ToCredential(ExecutionAssignmentMessage assignment)
        => new(assignment.RequestId, assignment.ProjectId, assignment.ClaimToken);

    internal IReadOnlyDictionary<Guid, ExecutionAssignmentMessage> ActiveAssignmentsSnapshot()
    {
        lock (_assignmentsLock)
        {
            return _activeAssignments.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Assignment);
        }
    }

    private Guid[] ActiveAssignmentRequestIdsSnapshot()
    {
        lock (_assignmentsLock)
        {
            return [.. _activeAssignments.Keys];
        }
    }

    private sealed class PendingAssignmentCancellation(string? rootSessionId, string reason)
    {
        public string? RootSessionId { get; set; } = rootSessionId;
        public string Reason { get; set; } = reason;
        public Task? Attempt { get; set; }
        public bool TerminalAccepted { get; set; }
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
