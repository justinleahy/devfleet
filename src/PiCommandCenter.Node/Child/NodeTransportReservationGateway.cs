using System.Collections.Concurrent;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Child;

/// <summary>
/// Production <see cref="INodeReservationGateway"/>: delegates every call to the Control Plane
/// node hub through <see cref="NodeTransportClient"/> reservation wrappers.
/// </summary>
public sealed class NodeTransportReservationGateway : INodeReservationGateway
{
    private readonly INodeReservationTransport _transport;
    private readonly INodeAssignmentCredentialSource _credentials;
    private readonly ConcurrentDictionary<Guid, NodeAssignmentCredential> _leaseCredentials = new();

    public NodeTransportReservationGateway(
        NodeTransportClient transport,
        INodeAssignmentCredentialSource credentials)
        : this(new NodeReservationTransport(transport), credentials)
    {
    }

    internal NodeTransportReservationGateway(
        INodeReservationTransport transport,
        INodeAssignmentCredentialSource credentials)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public async Task<ReservationOperationResult> AcquireAsync(
        Guid projectId,
        Guid requestId,
        string ownerSessionId,
        IReadOnlyList<ReservationScopeSpec> scopes,
        string reason,
        CancellationToken cancellationToken)
    {
        var credential = GetRequestCredential(requestId, projectId);
        var result = await _transport.AcquireReservationAsync(
            new AcquireReservationMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                ownerSessionId,
                ToScopes(scopes),
                reason),
            cancellationToken).ConfigureAwait(false);
        if (result.Error is null)
        {
            CacheLease(result.Lease, credential);
        }

        return ToResult(result);
    }

    public async Task<ReservationOperationResult> ExpandAsync(
        Guid leaseId,
        Guid projectId,
        long fencingToken,
        string sessionId,
        IReadOnlyList<ReservationScopeSpec> scopes,
        CancellationToken cancellationToken)
    {
        var credential = GetProjectCredential(projectId);
        var result = await _transport.ExpandReservationAsync(
            new ExpandReservationMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                leaseId,
                fencingToken,
                sessionId,
                ToScopes(scopes)),
            cancellationToken).ConfigureAwait(false);
        return ToResult(result);
    }

    public async Task<ReservationOperationResult> ReleaseAsync(
        Guid leaseId,
        Guid projectId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var credential = GetProjectCredential(projectId);
        var result = await _transport.ReleaseReservationAsync(
            new ReleaseReservationMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                leaseId,
                sessionId),
            cancellationToken).ConfigureAwait(false);
        return ToResult(result);
    }

    public async Task<ReservationOperationResult> TransferAsync(
        Guid leaseId,
        string fromSessionId,
        string toSessionId,
        CancellationToken cancellationToken)
    {
        var credential = GetLeaseCredential(leaseId);
        var result = await _transport.TransferReservationAsync(
            new TransferReservationMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                leaseId,
                fromSessionId,
                toSessionId),
            cancellationToken).ConfigureAwait(false);
        return ToResult(result);
    }

    public async Task<ReservationOperationResult> RenewAsync(
        Guid leaseId,
        long fencingToken,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var credential = GetLeaseCredential(leaseId);
        var result = await _transport.RenewReservationAsync(
            new ReservationMutationMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                leaseId,
                fencingToken,
                sessionId),
            cancellationToken).ConfigureAwait(false);
        return ToResult(result);
    }

    public async Task<MutationAuthorizationResult> AuthorizeAsync(
        Guid leaseId,
        long fencingToken,
        string sessionId,
        string targetPath,
        string operation,
        CancellationToken cancellationToken)
    {
        var credential = GetLeaseCredential(leaseId);
        var (code, name) = ToOperation(operation);
        var result = await _transport.AuthorizeMutationAsync(
            new MutationAuthorizationMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                leaseId,
                fencingToken,
                sessionId,
                targetPath,
                code,
                name),
            cancellationToken).ConfigureAwait(false);
        return new MutationAuthorizationResult(
            result.Authorized,
            result.Error is null ? null : new GatewayError(result.Error.Code, result.Error.Message));
    }

    public async Task<IReadOnlyList<ReservationLeaseInfo>> ListAsync(
        Guid projectId,
        bool includeReleased,
        CancellationToken cancellationToken)
    {
        var credential = GetProjectCredential(projectId);
        var leases = await _transport.ListReservationsAsync(
            new ListReservationsMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                includeReleased),
            cancellationToken).ConfigureAwait(false);
        foreach (var lease in leases)
        {
            CacheLease(lease, credential);
        }

        return [.. leases.Select(ToLease)];
    }

    public async Task<ReservationOperationResult> MarkRecoveryRequiredAsync(
        Guid leaseId,
        string reason,
        CancellationToken cancellationToken)
    {
        var credential = GetLeaseCredential(leaseId);
        var result = await _transport.MarkRecoveryRequiredAsync(
            new MarkRecoveryMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                leaseId,
                reason),
            cancellationToken).ConfigureAwait(false);
        return ToResult(result);
    }

    private static ReservationScopeMessage[] ToScopes(IReadOnlyList<ReservationScopeSpec> scopes)
        => [.. scopes.Select(s => new ReservationScopeMessage(
            s.Kind switch
            {
                "directory" => 1,
                "resource" => 2,
                _ => 0,
            },
            s.Kind,
            s.Path))];

    /// <summary>Maps a tool operation name onto the transport operation code.</summary>
    private static (int Code, string Name) ToOperation(string operation)
        => operation switch
        {
            "read" => (0, "read"),
            "write" => (1, "write"),
            "edit" => (2, "edit"),
            "delete" => (3, "delete"),
            "move" => (4, "move"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };

    private NodeAssignmentCredential GetRequestCredential(Guid requestId, Guid projectId)
    {
        if (_credentials.TryGetByRequest(requestId, out var credential)
            && credential.ProjectId == projectId)
        {
            return credential;
        }

        throw new InvalidOperationException(
            $"No active assignment credential is available for request '{requestId}' "
            + $"in project '{projectId}'.");
    }

    private NodeAssignmentCredential GetProjectCredential(Guid projectId)
    {
        if (_credentials.TryGetByProject(projectId, out var credential)
            && credential.ProjectId == projectId)
        {
            return credential;
        }

        throw new InvalidOperationException(
            $"No active assignment credential is available for project '{projectId}'.");
    }

    private NodeAssignmentCredential GetLeaseCredential(Guid leaseId)
    {
        if (_leaseCredentials.TryGetValue(leaseId, out var credential))
        {
            return credential;
        }

        throw new InvalidOperationException(
            $"No authenticated assignment credential is cached for lease '{leaseId}'.");
    }

    private void CacheLease(
        ReservationLeaseMessage? lease,
        NodeAssignmentCredential credential)
    {
        if (lease is not null)
        {
            _leaseCredentials[lease.LeaseId] = credential;
        }
    }

    private static ReservationOperationResult ToResult(ReservationOperationResultMessage result)
        => new(
            result.Lease is null ? null : ToLease(result.Lease),
            result.Error is null ? null : new GatewayError(result.Error.Code, result.Error.Message));

    private static ReservationLeaseInfo ToLease(ReservationLeaseMessage lease)
        => new(
            lease.LeaseId,
            lease.FencingToken,
            lease.StateName,
            lease.ExpiresAt,
            [.. lease.Scopes.Select(s => new ReservationScopeSpec(s.KindName, s.Path))],
            lease.OwnerSessionId);

    private sealed class NodeReservationTransport(NodeTransportClient transport)
        : INodeReservationTransport
    {
        private readonly NodeTransportClient _transport =
            transport ?? throw new ArgumentNullException(nameof(transport));

        public Task<ReservationOperationResultMessage> AcquireReservationAsync(
            AcquireReservationMessage message,
            CancellationToken cancellationToken)
            => _transport.AcquireReservationAsync(message, cancellationToken);

        public Task<ReservationOperationResultMessage> RenewReservationAsync(
            ReservationMutationMessage message,
            CancellationToken cancellationToken)
            => _transport.RenewReservationAsync(message, cancellationToken);

        public Task<ReservationOperationResultMessage> ExpandReservationAsync(
            ExpandReservationMessage message,
            CancellationToken cancellationToken)
            => _transport.ExpandReservationAsync(message, cancellationToken);

        public Task<ReservationOperationResultMessage> ReleaseReservationAsync(
            ReleaseReservationMessage message,
            CancellationToken cancellationToken)
            => _transport.ReleaseReservationAsync(message, cancellationToken);

        public Task<ReservationOperationResultMessage> TransferReservationAsync(
            TransferReservationMessage message,
            CancellationToken cancellationToken)
            => _transport.TransferReservationAsync(message, cancellationToken);

        public Task<MutationAuthorizationResultMessage> AuthorizeMutationAsync(
            MutationAuthorizationMessage message,
            CancellationToken cancellationToken)
            => _transport.AuthorizeMutationAsync(message, cancellationToken);

        public Task<ReservationOperationResultMessage> MarkRecoveryRequiredAsync(
            MarkRecoveryMessage message,
            CancellationToken cancellationToken)
            => _transport.MarkRecoveryRequiredAsync(message, cancellationToken);

        public Task<ReservationLeaseMessage[]> ListReservationsAsync(
            ListReservationsMessage message,
            CancellationToken cancellationToken)
            => _transport.ListReservationsAsync(message, cancellationToken);
    }
}

internal interface INodeReservationTransport
{
    Task<ReservationOperationResultMessage> AcquireReservationAsync(
        AcquireReservationMessage message,
        CancellationToken cancellationToken);

    Task<ReservationOperationResultMessage> RenewReservationAsync(
        ReservationMutationMessage message,
        CancellationToken cancellationToken);

    Task<ReservationOperationResultMessage> ExpandReservationAsync(
        ExpandReservationMessage message,
        CancellationToken cancellationToken);

    Task<ReservationOperationResultMessage> ReleaseReservationAsync(
        ReleaseReservationMessage message,
        CancellationToken cancellationToken);

    Task<ReservationOperationResultMessage> TransferReservationAsync(
        TransferReservationMessage message,
        CancellationToken cancellationToken);

    Task<MutationAuthorizationResultMessage> AuthorizeMutationAsync(
        MutationAuthorizationMessage message,
        CancellationToken cancellationToken);

    Task<ReservationOperationResultMessage> MarkRecoveryRequiredAsync(
        MarkRecoveryMessage message,
        CancellationToken cancellationToken);

    Task<ReservationLeaseMessage[]> ListReservationsAsync(
        ListReservationsMessage message,
        CancellationToken cancellationToken);
}
