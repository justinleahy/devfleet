using PiCommandCenter.Application.Mail;
using PiCommandCenter.Domain;
using PiCommandCenter.Node.Child;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// In-memory reservation gateway fake: records acquire/authorize calls, grants leases on
/// demand, and can deny or stale-token any single authorization for denial tests.
/// </summary>
public sealed class FakeReservationGateway : INodeReservationGateway
{
    public List<(Guid ProjectId, Guid RequestId, string OwnerSessionId, IReadOnlyList<ReservationScopeSpec> Scopes)> Acquires { get; } = [];
    public List<(string Path, string Operation)> Authorizations { get; } = [];
    public List<(Guid LeaseId, long FencingToken, string SessionId)> Renewals { get; } = [];
    public List<(Guid LeaseId, string FromSessionId, string ToSessionId)> Transfers { get; } = [];
    public List<Guid> Releases { get; } = [];
    public List<(Guid LeaseId, string Reason)> Recoveries { get; } = [];
    public GatewayError? AcquireError { get; set; }
    public int RenewFailuresRemaining { get; set; }
    public int ListCount { get; private set; }
    public (Guid ProjectId, Guid RequestId, string OwnerSessionId, IReadOnlyList<ReservationScopeSpec> Scopes)? LastAcquire { get; private set; }

    /// <summary>When set, overrides the authorization decision for matching (path, operation).</summary>
    public Func<string, string, MutationAuthorizationResult?>? OnAuthorize { get; set; }
    public Func<CancellationToken, Task>? OnReleaseAsync { get; set; }

    public ReservationLeaseInfo GrantLease(
        string ownerSessionId = "root-session-1",
        params ReservationScopeSpec[] scopes)
    {
        var lease = new ReservationLeaseInfo(
            Guid.NewGuid(), 42, "Active", DateTimeOffset.UtcNow.AddMinutes(2), scopes, ownerSessionId);
        _granted[lease.LeaseId] = lease;
        return lease;
    }

    public void Seed(ReservationLeaseInfo lease) => _granted[lease.LeaseId] = lease;

    private readonly Dictionary<Guid, ReservationLeaseInfo> _granted = new();

    public Task<ReservationOperationResult> AcquireAsync(
        Guid projectId,
        Guid requestId,
        string ownerSessionId,
        IReadOnlyList<ReservationScopeSpec> scopes,
        string reason,
        CancellationToken cancellationToken)
    {
        var acquire = (projectId, requestId, ownerSessionId, scopes);
        Acquires.Add(acquire);
        LastAcquire = acquire;
        if (AcquireError is not null)
        {
            return Task.FromResult(new ReservationOperationResult(null, AcquireError));
        }

        var lease = new ReservationLeaseInfo(
            Guid.NewGuid(), 7, "Active", DateTimeOffset.UtcNow.AddMinutes(2), scopes, ownerSessionId);
        _granted[lease.LeaseId] = lease;
        return Task.FromResult(new ReservationOperationResult(lease, null));
    }

    public Task<ReservationOperationResult> ExpandAsync(
        Guid leaseId,
        Guid projectId,
        long fencingToken,
        string sessionId,
        IReadOnlyList<ReservationScopeSpec> scopes,
        CancellationToken cancellationToken)
    {
        if (!_granted.TryGetValue(leaseId, out var lease))
        {
            return Task.FromResult(new ReservationOperationResult(
                null, new GatewayError("not_found", "No such lease.")));
        }

        var expanded = lease with { Scopes = [.. lease.Scopes, .. scopes] };
        _granted[leaseId] = expanded;
        return Task.FromResult(new ReservationOperationResult(expanded, null));
    }

    public async Task<ReservationOperationResult> ReleaseAsync(
        Guid leaseId,
        Guid projectId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        Releases.Add(leaseId);
        if (OnReleaseAsync is not null)
        {
            await OnReleaseAsync(cancellationToken);
        }

        return _granted.Remove(leaseId)
            ? new ReservationOperationResult(
                new ReservationLeaseInfo(leaseId, 0, "Released", DateTimeOffset.UtcNow, []),
                null)
            : new ReservationOperationResult(
                null, new GatewayError("not_found", "No such lease."));
    }

    public Task<ReservationOperationResult> TransferAsync(
        Guid leaseId,
        string fromSessionId,
        string toSessionId,
        CancellationToken cancellationToken)
    {
        Transfers.Add((leaseId, fromSessionId, toSessionId));
        return Task.FromResult(_granted.TryGetValue(leaseId, out var lease)
            ? new ReservationOperationResult(lease, null)
            : new ReservationOperationResult(
                null, new GatewayError("not_found", "No such lease.")));
    }

    public Task<ReservationOperationResult> RenewAsync(
        Guid leaseId,
        long fencingToken,
        string sessionId,
        CancellationToken cancellationToken)
    {
        Renewals.Add((leaseId, fencingToken, sessionId));
        if (RenewFailuresRemaining > 0)
        {
            RenewFailuresRemaining--;
            throw new IOException("simulated transport disconnect");
        }

        if (!_granted.TryGetValue(leaseId, out var lease))
        {
            return Task.FromResult(new ReservationOperationResult(
                null, new GatewayError("not_found", "No such lease.")));
        }

        var renewed = lease with { FencingToken = lease.FencingToken + 1 };
        _granted[leaseId] = renewed;
        return Task.FromResult(new ReservationOperationResult(renewed, null));
    }

    public Task<MutationAuthorizationResult> AuthorizeAsync(
        Guid leaseId,
        long fencingToken,
        string sessionId,
        string targetPath,
        string operation,
        CancellationToken cancellationToken)
    {
        Authorizations.Add((targetPath, operation));
        var overridden = OnAuthorize?.Invoke(targetPath, operation);
        if (overridden is not null)
        {
            return Task.FromResult(overridden);
        }

        if (!_granted.TryGetValue(leaseId, out var lease))
        {
            return Task.FromResult(new MutationAuthorizationResult(
                false, new GatewayError("not_found", "No such lease.")));
        }

        if (lease.FencingToken != fencingToken)
        {
            return Task.FromResult(new MutationAuthorizationResult(
                false, new GatewayError("invalid_fencing_token", "The fencing token is stale.")));
        }

        return Task.FromResult(new MutationAuthorizationResult(true, null));
    }

    public Task<IReadOnlyList<ReservationLeaseInfo>> ListAsync(
        Guid projectId,
        bool includeReleased,
        CancellationToken cancellationToken)
    {
        ListCount++;
        return Task.FromResult<IReadOnlyList<ReservationLeaseInfo>>([.. _granted.Values]);
    }

    public Task<ReservationOperationResult> MarkRecoveryRequiredAsync(
        Guid leaseId,
        string reason,
        CancellationToken cancellationToken)
    {
        Recoveries.Add((leaseId, reason));
        if (!_granted.TryGetValue(leaseId, out var lease))
        {
            return Task.FromResult(new ReservationOperationResult(
                null, new GatewayError("not_found", "No such lease.")));
        }

        var marked = lease with { State = "RecoveryRequired" };
        _granted[leaseId] = marked;
        return Task.FromResult(new ReservationOperationResult(marked, null));
    }
}

/// <summary>In-memory mail gateway fake recording sends and acknowledging per session.</summary>
public sealed class FakeMailGateway : INodeMailGateway
{
    public List<(string SenderSessionId, IReadOnlyList<string> Recipients, string Subject)> Sends { get; } = [];

    public Task<MailDeliveryResult> SendAsync(
        Guid projectId,
        Guid requestId,
        string? threadId,
        string senderSessionId,
        IReadOnlyList<string> recipients,
        string subject,
        string bodyMarkdown,
        string importance,
        bool ackRequired,
        string? inReplyToMessageId,
        CancellationToken cancellationToken)
    {
        Sends.Add((senderSessionId, recipients, subject));
        return Task.FromResult(new MailDeliveryResult(
            Guid.NewGuid().ToString("N"), threadId ?? "thread-1", recipients));
    }

    public Task<MailInboxResult> FetchInboxAsync(
        Guid projectId,
        string recipientSessionId,
        int maxCount,
        CancellationToken cancellationToken)
        => Task.FromResult(new MailInboxResult([]));

    public Task<MailInboxResult> FetchThreadAsync(
        Guid projectId,
        string recipientSessionId,
        string threadId,
        CancellationToken cancellationToken)
        => Task.FromResult(new MailInboxResult([]));

    public Task<MailReceiptResult> MarkReadAsync(
        string recipientSessionId,
        string messageId,
        CancellationToken cancellationToken)
        => Task.FromResult(new MailReceiptResult(messageId, recipientSessionId));

    public Task<MailReceiptResult> AcknowledgeAsync(
        string recipientSessionId,
        string messageId,
        CancellationToken cancellationToken)
        => Task.FromResult(new MailReceiptResult(messageId, recipientSessionId));
}

/// <summary>In-memory agent identity registry fake recording allocations and releases.</summary>
public sealed class FakeIdentityRegistry : IAgentIdentityRegistry
{
    public List<(string SessionId, string AgentName, string Role, string Runtime)> Allocated { get; } = [];
    public List<string> Released { get; } = [];
    public string? AllocatedNameOverride { get; set; }


    public Task<AgentIdentityDto> AllocateAsync(
        AllocateAgentIdentityCommand command, CancellationToken cancellationToken = default)
    {
        Allocated.Add((command.SessionId, command.RequestedName, command.Role, command.Runtime));
        return Task.FromResult(new AgentIdentityDto(
            command.ProjectId,
            command.SessionId,
            AllocatedNameOverride ?? command.RequestedName,
            command.Role,
            command.Runtime,
            DateTimeOffset.UtcNow));
    }

    public Task ReleaseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        Released.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task<AgentIdentityDto?> FindByNameAsync(
        ProjectId projectId, string agentName, CancellationToken cancellationToken = default)
        => Task.FromResult<AgentIdentityDto?>(null);
}

/// <summary>
/// No-op stubs for the supervisor's extended dependencies (verification, repository
/// inspection, crash recovery, completion gate, runtime registry); the child supervisor tests
/// never exercise those tool paths.
/// </summary>
public sealed class NoopVerificationCoordinator : PiCommandCenter.Node.Verification.IRequestVerificationCoordinator
{
    public Task<PiCommandCenter.Node.Verification.RequestVerificationDecision> VerifyFinalAsync(
        PiCommandCenter.Node.Verification.RequestVerificationContext context,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Not exercised by these tests.");

    public Task<PiCommandCenter.Node.Verification.RequestVerificationDecision> VerifyIntermediateAsync(
        PiCommandCenter.Node.Verification.RequestVerificationContext context,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Not exercised by these tests.");

    public Task<string> CaptureFingerprintAsync(
        PiCommandCenter.Node.Verification.RequestVerificationContext context,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Not exercised by these tests.");
}

public sealed class NoopRepositoryInspector : PiCommandCenter.Node.Repository.IRepositoryInspector
{
    public Task<PiCommandCenter.Node.Repository.RepositoryBaseline> CaptureBaselineAsync(
        string repositoryRoot, bool requireCleanStart, bool allowUntrackedFiles,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Not exercised by these tests.");

    public Task<PiCommandCenter.Node.Repository.RepositoryDiffInspection> InspectDiffAsync(
        string repositoryRoot, string baseCommit,
        IReadOnlyList<PiCommandCenter.Node.Child.ReservationLeaseInfo> leases,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Not exercised by these tests.");

    public Task DetectExternalChangesAsync(
        string repositoryRoot, string baseCommit,
        IReadOnlyList<PiCommandCenter.Node.Child.ReservationLeaseInfo> leases,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class NoopCrashRecovery : PiCommandCenter.Node.Repository.IRuntimeCrashRecovery
{
    public Task MarkOwnedLeasesRecoveryRequiredAsync(
        Guid nodeId, Guid projectId, Guid? requestId, string ownerSessionId, string reason,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class NoopCompletionGateway : PiCommandCenter.Node.Child.INodeCompletionGateway
{
    public Task RecordVerificationRunAsync(
        string sessionId,
        PiCommandCenter.Application.Verification.VerificationRunDto run,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<IReadOnlyList<PiCommandCenter.Application.Verification.VerificationRunDto>> ListVerificationRunsAsync(
        string sessionId,
        Guid projectId,
        Guid requestId,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PiCommandCenter.Application.Verification.VerificationRunDto>>([]);

    public Task<PiCommandCenter.Application.Completion.CompletionGateDecision> BeginTerminalizationAsync(
        Guid projectId,
        Guid requestId,
        string? rootSessionId,
        PiCommandCenter.Contracts.NodeTransport.TerminalizationIntent intent,
        PiCommandCenter.Application.Completion.CompletionEvidence? evidence,
        string? reason,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Not exercised by these tests.");

    public Task<PiCommandCenter.Application.Completion.CompletionGateDecision> ConfirmTerminalizationAsync(
        Guid projectId,
        Guid requestId,
        string? rootSessionId,
        PiCommandCenter.Contracts.NodeTransport.TerminalizationIntent intent,
        PiCommandCenter.Application.Completion.CompletionEvidence? evidence,
        string? reason,
        PiCommandCenter.Application.Completion.AssignmentQuiescenceProof proof,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Not exercised by these tests.");
}
