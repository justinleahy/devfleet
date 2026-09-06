using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Recovery;

/// <summary>
/// Named recovery stages emitted on progress. Order is the automatic-recovery sequence.
/// </summary>
internal static class AssignmentRecoveryStages
{
    public const string JournalIntent = "journal_intent";
    public const string CloseAdmission = "close_admission";
    public const string CooperativeStop = "cooperative_stop";
    public const string IsolatedProcessStop = "isolated_process_stop";
    public const string DrainOperations = "drain_operations";
    public const string FlushEvents = "flush_events";
    public const string InspectRepository = "inspect_repository";
    public const string ResolveReservations = "resolve_reservations";
}

internal sealed record AssignmentRecoveryProcessSnapshot(
    RecoveryKnownCountMessage Processes,
    IReadOnlyList<RecoveryProcessIdentityMessage> Identities);

internal sealed record AssignmentRecoveryEventFlushResult(
    RecoveryKnownCountMessage PendingEvents,
    long? EventAcknowledgementPosition,
    string? EventAcknowledgementUnknownReasonCode);

internal sealed record AssignmentRecoveryInventorySnapshot(
    RecoveryKnownCountMessage Children,
    RecoveryKnownCountMessage Operations,
    RecoveryKnownCountMessage Processes,
    RecoveryKnownCountMessage PendingEvents,
    RecoveryKnownCountMessage Reservations);

/// <summary>
/// Assignment-bound stop primitives. Production adapters and test fakes satisfy this
/// seam; the runner never talks to EF or deletes spool/workspace contents.
/// </summary>
internal interface IAssignmentRecoveryRuntime
{
    Task JournalRecoveryIntentAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken);

    Task CloseAdmissionAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken);

    Task RequestCooperativeStopAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken);

    Task<AssignmentRecoveryProcessSnapshot> StopIsolatedProcessesAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken);

    Task DrainSupervisedOperationsAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken);

    Task<AssignmentRecoveryEventFlushResult> FlushAcknowledgedEventsAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken);

    Task<RecoveryRepositoryStatusMessage> InspectRepositoryAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RecoveryReservationDispositionMessage>> ResolveReservationsAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken);

    Task<AssignmentRecoveryInventorySnapshot> ObserveInventoryAsync(
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken);
}


/// <summary>
/// Public recovery-runner contract. NodeWorker depends on this seam so callers
/// never construct or name the internal implementation type.
/// </summary>
public interface IAssignmentRecoveryRunner
{
    Task<AssignmentRecoveryProofMessage> RunAsync(
        RecoverAssignmentCommandMessage command,
        Func<AssignmentRecoveryProgressMessage, CancellationToken, Task>? onProgress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounded idempotent RecoverAssignment runner. Concurrent same recovery/attempt
/// shares one task; a later attempt starts only after the prior attempt finishes.
/// Timeouts are enforced outside runtime calls and surface as unknown inventories,
/// never as known zero.
/// </summary>
internal sealed class AssignmentRecoveryRunner : IAssignmentRecoveryRunner
{
    private readonly IAssignmentRecoveryRuntime _runtime;
    private readonly IOptions<NodeOptions> _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, RequestAttemptGate> _gates = new();

    public AssignmentRecoveryRunner(
        IAssignmentRecoveryRuntime runtime,
        IOptions<NodeOptions> options,
        TimeProvider time)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public Task<AssignmentRecoveryProofMessage> RunAsync(
        RecoverAssignmentCommandMessage command,
        Func<AssignmentRecoveryProgressMessage, CancellationToken, Task>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var gate = _gates.GetOrAdd(command.RequestId, static _ => new RequestAttemptGate());
        return gate.RunAsync(command, inner => ExecuteAttemptAsync(inner, onProgress, cancellationToken));
    }

    private async Task<AssignmentRecoveryProofMessage> ExecuteAttemptAsync(
        RecoverAssignmentCommandMessage command,
        Func<AssignmentRecoveryProgressMessage, CancellationToken, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = command.Deadline - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            attemptCts.Cancel();
        }
        else
        {
            _ = _time.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                attemptCts,
                remaining,
                Timeout.InfiniteTimeSpan);
        }

        var deadlineToken = attemptCts.Token;
        var children = Unknown(RecoveryReasonCodes.ProcessStopUnproven);
        var operations = Unknown(RecoveryReasonCodes.OperationDrainTimeout);
        var processes = Unknown(RecoveryReasonCodes.ProcessStopUnproven);
        var pendingEvents = Unknown(RecoveryReasonCodes.EventsUnacknowledged);
        var reservations = Unknown(RecoveryReasonCodes.ReservationUnresolved);
        IReadOnlyList<RecoveryProcessIdentityMessage> identities = [];
        IReadOnlyList<RecoveryReservationDispositionMessage> dispositions = [];
        RecoveryRepositoryStatusMessage? repository = null;
        long? ackPosition = null;
        string? ackUnknown = RecoveryReasonCodes.EventsUnacknowledged;
        var admissionClosed = false;
        var processStopProven = false;
        var reasons = new List<string>();

        async Task EmitAsync(string stage)
        {
            if (onProgress is null)
            {
                return;
            }

            AssignmentRecoveryInventorySnapshot? observed = null;
            try
            {
                observed = await InvokeAsync(
                        ct => _runtime.ObserveInventoryAsync(command, ct),
                        deadlineToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            var progress = new AssignmentRecoveryProgressMessage(
                command.RecoveryId,
                command.Attempt,
                command.ProjectId,
                command.RequestId,
                command.ClaimToken,
                command.BindingRevision,
                _time.GetUtcNow(),
                stage,
                observed?.Children ?? children,
                observed?.Operations ?? operations,
                observed?.Processes ?? processes,
                observed?.PendingEvents ?? pendingEvents,
                observed?.Reservations ?? reservations,
                reasons.ToArray());
            await onProgress(progress, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await InvokeAsync(ct => _runtime.JournalRecoveryIntentAsync(command, ct), deadlineToken)
                .ConfigureAwait(false);
            await EmitAsync(AssignmentRecoveryStages.JournalIntent).ConfigureAwait(false);

            await InvokeAsync(ct => _runtime.CloseAdmissionAsync(command, ct), deadlineToken)
                .ConfigureAwait(false);
            admissionClosed = true;
            await EmitAsync(AssignmentRecoveryStages.CloseAdmission).ConfigureAwait(false);

            await InvokeAsync(ct => _runtime.RequestCooperativeStopAsync(command, ct), deadlineToken)
                .ConfigureAwait(false);
            await WaitCooperativeBudgetAsync(deadlineToken).ConfigureAwait(false);
            await EmitAsync(AssignmentRecoveryStages.CooperativeStop).ConfigureAwait(false);

            try
            {
                var snapshot = await InvokeAsync(
                        ct => _runtime.StopIsolatedProcessesAsync(command, ct),
                        deadlineToken)
                    .ConfigureAwait(false);
                processes = RequireValid(snapshot.Processes, RecoveryReasonCodes.ProcessStopUnproven);
                identities = snapshot.Identities;
                processStopProven = processes.IsKnown
                    && processes.Value == 0
                    && identities.All(static p => !p.EscapedDescendant);
                if (!processStopProven)
                {
                    AddReason(reasons, RecoveryReasonCodes.ProcessStopUnproven);
                    if (processes.IsKnown && processes.Value == 0 && identities.Any(static p => p.EscapedDescendant))
                    {
                        processes = Unknown(RecoveryReasonCodes.ProcessStopUnproven);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                processes = Unknown(RecoveryReasonCodes.ProcessStopUnproven);
                AddReason(reasons, RecoveryReasonCodes.ProcessStopUnproven);
            }

            await EmitAsync(AssignmentRecoveryStages.IsolatedProcessStop).ConfigureAwait(false);

            try
            {
                await InvokeAsync(ct => _runtime.DrainSupervisedOperationsAsync(command, ct), deadlineToken)
                    .ConfigureAwait(false);
                operations = Known(0);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                operations = Unknown(RecoveryReasonCodes.OperationDrainTimeout);
                AddReason(reasons, RecoveryReasonCodes.OperationDrainTimeout);
            }

            await EmitAsync(AssignmentRecoveryStages.DrainOperations).ConfigureAwait(false);

            try
            {
                var flushed = await InvokeAsync(
                        ct => _runtime.FlushAcknowledgedEventsAsync(command, ct),
                        deadlineToken)
                    .ConfigureAwait(false);
                pendingEvents = RequireValid(flushed.PendingEvents, RecoveryReasonCodes.EventsUnacknowledged);
                ackPosition = flushed.EventAcknowledgementPosition;
                ackUnknown = flushed.EventAcknowledgementUnknownReasonCode;
                if (pendingEvents.IsUnknown
                    || (pendingEvents.IsKnown && pendingEvents.Value != 0)
                    || ackPosition is null
                    || !string.IsNullOrWhiteSpace(ackUnknown))
                {
                    AddReason(reasons, RecoveryReasonCodes.EventsUnacknowledged);
                    if (pendingEvents.IsKnown && pendingEvents.Value == 0 && ackPosition is null)
                    {
                        pendingEvents = Unknown(RecoveryReasonCodes.EventsUnacknowledged);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                pendingEvents = Unknown(RecoveryReasonCodes.EventsUnacknowledged);
                ackPosition = null;
                ackUnknown = RecoveryReasonCodes.EventsUnacknowledged;
                AddReason(reasons, RecoveryReasonCodes.EventsUnacknowledged);
            }

            await EmitAsync(AssignmentRecoveryStages.FlushEvents).ConfigureAwait(false);

            try
            {
                repository = await InvokeAsync(
                        ct => _runtime.InspectRepositoryAsync(command, ct),
                        deadlineToken)
                    .ConfigureAwait(false);
                if (!repository.Available)
                {
                    AddReason(reasons, RecoveryReasonCodes.RepositoryStatusUnknown);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                repository = null;
                AddReason(reasons, RecoveryReasonCodes.RepositoryStatusUnknown);
            }

            await EmitAsync(AssignmentRecoveryStages.InspectRepository).ConfigureAwait(false);

            if (processStopProven)
            {
                try
                {
                    dispositions = await InvokeAsync(
                            ct => _runtime.ResolveReservationsAsync(command, ct),
                            deadlineToken)
                        .ConfigureAwait(false);
                    var unresolved = dispositions.Any(static d =>
                        !string.Equals(d.Disposition, "released", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(d.Disposition, "resolved", StringComparison.OrdinalIgnoreCase));
                    reservations = unresolved
                        ? Unknown(RecoveryReasonCodes.ReservationUnresolved)
                        : Known(0);
                    if (unresolved)
                    {
                        AddReason(reasons, RecoveryReasonCodes.ReservationUnresolved);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    reservations = Unknown(RecoveryReasonCodes.ReservationUnresolved);
                    AddReason(reasons, RecoveryReasonCodes.ReservationUnresolved);
                }
            }
            else
            {
                reservations = Unknown(RecoveryReasonCodes.ReservationUnresolved);
                AddReason(reasons, RecoveryReasonCodes.ReservationUnresolved);
            }

            await EmitAsync(AssignmentRecoveryStages.ResolveReservations).ConfigureAwait(false);

            try
            {
                var observed = await InvokeAsync(
                        ct => _runtime.ObserveInventoryAsync(command, ct),
                        deadlineToken)
                    .ConfigureAwait(false);
                children = RequireValid(observed.Children, RecoveryReasonCodes.ProcessStopUnproven);
                if (operations.IsKnown)
                {
                    operations = RequireValid(observed.Operations, RecoveryReasonCodes.OperationDrainTimeout);
                }

                if (children.IsUnknown || (children.IsKnown && children.Value != 0))
                {
                    AddReason(reasons, RecoveryReasonCodes.ProcessStopUnproven);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                children = Unknown(RecoveryReasonCodes.ProcessStopUnproven);
                AddReason(reasons, RecoveryReasonCodes.ProcessStopUnproven);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            AddReason(reasons, RecoveryReasonCodes.ProcessStopUnproven);
        }

        if (deadlineToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            children = CoerceTimeoutUnknown(children, RecoveryReasonCodes.ProcessStopUnproven);
            operations = CoerceTimeoutUnknown(operations, RecoveryReasonCodes.OperationDrainTimeout);
            processes = CoerceTimeoutUnknown(processes, RecoveryReasonCodes.ProcessStopUnproven);
            pendingEvents = CoerceTimeoutUnknown(pendingEvents, RecoveryReasonCodes.EventsUnacknowledged);
            reservations = CoerceTimeoutUnknown(reservations, RecoveryReasonCodes.ReservationUnresolved);
            if (ackPosition is null && string.IsNullOrWhiteSpace(ackUnknown))
            {
                ackUnknown = RecoveryReasonCodes.EventsUnacknowledged;
            }

            AddReason(reasons, RecoveryReasonCodes.ProcessStopUnproven);
        }

        return new AssignmentRecoveryProofMessage(
            command.RecoveryId,
            command.Attempt,
            command.ProjectId,
            command.RequestId,
            command.ClaimToken,
            command.BindingRevision,
            _time.GetUtcNow(),
            admissionClosed,
            children,
            operations,
            processes,
            pendingEvents,
            reservations,
            ackPosition,
            ackUnknown,
            identities,
            dispositions,
            repository);
    }

    private async Task WaitCooperativeBudgetAsync(CancellationToken deadlineToken)
    {
        var budget = TimeSpan.FromSeconds(Math.Max(0, _options.Value.RecoveryCooperativeStopSeconds));
        if (budget <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await Task.Delay(budget, _time, deadlineToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!deadlineToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task InvokeAsync(Func<CancellationToken, Task> call, CancellationToken deadlineToken)
    {
        await call(deadlineToken).WaitAsync(deadlineToken).ConfigureAwait(false);
    }

    private static async Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken deadlineToken)
    {
        return await call(deadlineToken).WaitAsync(deadlineToken).ConfigureAwait(false);
    }

    private static RecoveryKnownCountMessage Known(int value) => new(value, null);

    private static RecoveryKnownCountMessage Unknown(string code) => new(null, code);

    private static RecoveryKnownCountMessage RequireValid(RecoveryKnownCountMessage count, string fallback)
    {
        if (count.IsValid)
        {
            return count;
        }

        return Unknown(fallback);
    }

    private static RecoveryKnownCountMessage CoerceTimeoutUnknown(
        RecoveryKnownCountMessage count,
        string code)
    {
        if (count.IsKnown)
        {
            return count;
        }

        return Unknown(code);
    }

    private static void AddReason(List<string> reasons, string code)
    {
        if (!reasons.Contains(code, StringComparer.Ordinal))
        {
            reasons.Add(code);
        }
    }

    private sealed class RequestAttemptGate
    {
        private readonly object _lock = new();
        private int _attempt;
        private Task<AssignmentRecoveryProofMessage>? _current;

        public Task<AssignmentRecoveryProofMessage> RunAsync(
            RecoverAssignmentCommandMessage command,
            Func<RecoverAssignmentCommandMessage, Task<AssignmentRecoveryProofMessage>> execute)
        {
            Task<AssignmentRecoveryProofMessage>? prior;
            lock (_lock)
            {
                if (_current is not null && _attempt == command.Attempt)
                {
                    return _current;
                }

                prior = _current;
            }

            return RunSerializedAsync(command, prior, execute);
        }

        private async Task<AssignmentRecoveryProofMessage> RunSerializedAsync(
            RecoverAssignmentCommandMessage command,
            Task<AssignmentRecoveryProofMessage>? prior,
            Func<RecoverAssignmentCommandMessage, Task<AssignmentRecoveryProofMessage>> execute)
        {
            if (prior is not null)
            {
                try
                {
                    await prior.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            Task<AssignmentRecoveryProofMessage> started;
            lock (_lock)
            {
                if (_current is not null && _attempt == command.Attempt)
                {
                    started = _current;
                }
                else
                {
                    _attempt = command.Attempt;
                    started = execute(command);
                    _current = started;
                }

            }

            return await started.ConfigureAwait(false);
        }
    }
}
