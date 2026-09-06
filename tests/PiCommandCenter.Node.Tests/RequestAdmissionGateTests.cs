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
}
