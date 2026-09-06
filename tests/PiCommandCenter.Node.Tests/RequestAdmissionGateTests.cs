using PiCommandCenter.Node.Quiescence;

namespace PiCommandCenter.Node.Tests;

public sealed class RequestAdmissionGateTests
{
    [Fact]
    public void Committed_tombstone_outlives_activity_until_runtime_callbacks_stop()
    {
        var gate = new RequestAdmissionGate(TimeProvider.System);
        var requestId = Guid.NewGuid();
        var callback = Assert.IsType<RequestCallbackLease>(
            gate.TryRegisterCallbackSource(requestId, "root-session"));
        var activity = Assert.IsType<NodeActivityLease>(
            gate.TryEnterOperation(requestId, "tool callback"));

        gate.CloseAdmission(requestId);
        gate.CommitTerminalization(requestId);
        activity.Dispose();

        Assert.True(gate.IsAdmissionClosed(requestId));
        Assert.Null(gate.TryEnterOperation(requestId, "late callback"));
        Assert.Null(gate.TryRegisterCallbackSource(requestId, "late-root"));

        callback.Dispose();

        Assert.False(gate.IsAdmissionClosed(requestId));
        using var newLifecycle = Assert.IsType<NodeActivityLease>(
            gate.TryEnterOperation(requestId, "new lifecycle"));
    }

    [Fact]
    public void Terminalization_preflight_is_exclusive_and_reopens_after_rejection()
    {
        var gate = new RequestAdmissionGate(TimeProvider.System);
        var requestId = Guid.NewGuid();
        var first = Assert.IsType<NodeActivityLease>(
            gate.TryEnterTerminalization(requestId, "first"));

        Assert.Null(gate.TryEnterTerminalization(requestId, "concurrent"));

        first.Dispose();

        using var retry = Assert.IsType<NodeActivityLease>(
            gate.TryEnterTerminalization(requestId, "retry"));
    }

    [Fact]
    public void Seal_rejects_new_mutations_and_unseals_after_release()
    {
        var gate = new RequestAdmissionGate(TimeProvider.System);
        var requestId = Guid.NewGuid();
        using var preexisting = Assert.IsType<NodeActivityLease>(
            gate.TryEnterOperation(requestId, "pre-barrier"));

        Assert.True(gate.TrySealAdmission(requestId));
        Assert.False(gate.TrySealAdmission(requestId));
        Assert.Null(gate.TryEnterOperation(requestId, "post-barrier"));
        Assert.Null(gate.TryAdmitChild(requestId, "child"));
        Assert.Null(gate.TryRegisterCallbackSource(requestId, "root"));
        Assert.False(gate.IsAdmissionClosed(requestId));

        preexisting.Dispose();
        gate.UnsealAdmission(requestId);

        using var retry = Assert.IsType<NodeActivityLease>(
            gate.TryEnterOperation(requestId, "after-unseal"));
    }

    [Fact]
    public async Task WaitUntilDrained_completes_after_preexisting_activity_releases()
    {
        var gate = new RequestAdmissionGate(TimeProvider.System);
        var requestId = Guid.NewGuid();
        var preexisting = Assert.IsType<NodeActivityLease>(
            gate.TryEnterOperation(requestId, "held"));

        Assert.True(gate.TrySealAdmission(requestId));
        var draining = gate.WaitUntilDrainedAsync(requestId, TimeSpan.FromSeconds(2));
        preexisting.Dispose();

        Assert.True(await draining);
        gate.UnsealAdmission(requestId);
    }

    [Fact]
    public void Unseal_does_not_reopen_committed_tombstone()
    {
        var gate = new RequestAdmissionGate(TimeProvider.System);
        var requestId = Guid.NewGuid();
        Assert.True(gate.TrySealAdmission(requestId));
        gate.CloseAdmission(requestId);
        gate.CommitTerminalization(requestId);
        gate.UnsealAdmission(requestId);

        Assert.True(gate.IsAdmissionClosed(requestId));
        Assert.Null(gate.TryEnterOperation(requestId, "after-commit"));
    }

    [Fact]
    public void Unseal_does_not_reopen_closed_admission()
    {
        var gate = new RequestAdmissionGate(TimeProvider.System);
        var requestId = Guid.NewGuid();
        Assert.True(gate.TrySealAdmission(requestId));
        gate.CloseAdmission(requestId);
        gate.UnsealAdmission(requestId);

        Assert.True(gate.IsAdmissionClosed(requestId));
        Assert.Null(gate.TryEnterOperation(requestId, "after-close"));
        Assert.False(gate.TrySealAdmission(requestId));
    }

    [Fact]
    public void Concurrent_unseal_cannot_reopen_closed_admission()
    {
        var gate = new RequestAdmissionGate(TimeProvider.System);
        var requestId = Guid.NewGuid();
        Assert.True(gate.TrySealAdmission(requestId));

        Parallel.Invoke(
            () => gate.CloseAdmission(requestId),
            () => gate.UnsealAdmission(requestId),
            () => gate.CommitTerminalization(requestId),
            () => gate.UnsealAdmission(requestId));

        Assert.True(gate.IsAdmissionClosed(requestId));
        Assert.Null(gate.TryEnterOperation(requestId, "after-race"));
        Assert.Null(gate.TryAdmitChild(requestId, "child"));
        Assert.Null(gate.TryRegisterCallbackSource(requestId, "root"));
    }

    [Fact]
    public async Task Terminalization_lease_does_not_block_drain_counts()
    {
        var gate = new RequestAdmissionGate(TimeProvider.System);
        var requestId = Guid.NewGuid();
        using var terminalization = Assert.IsType<NodeActivityLease>(
            gate.TryEnterTerminalization(requestId, "complete"));

        Assert.True(await gate.WaitUntilDrainedAsync(requestId, TimeSpan.Zero));
    }
}
