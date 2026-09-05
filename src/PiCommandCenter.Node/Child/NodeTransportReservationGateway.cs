using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Child;

/// <summary>
/// Production <see cref="INodeReservationGateway"/>: delegates every call to the Control Plane
/// node hub through <see cref="NodeTransportClient"/> reservation wrappers.
/// </summary>
public sealed class NodeTransportReservationGateway : INodeReservationGateway
{
    private readonly NodeTransportClient _transport;

    public NodeTransportReservationGateway(NodeTransportClient transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public Task<ReservationOperationResult> AcquireAsync(
        Guid projectId,
        Guid requestId,
        string ownerSessionId,
        IReadOnlyList<ReservationScopeSpec> scopes,
        string reason,
        CancellationToken cancellationToken)
        => InvokeAsync(_transport.AcquireReservationAsync(
            new AcquireReservationMessage(
                projectId, requestId, ownerSessionId, ToScopes(scopes), reason),
            cancellationToken));

    public Task<ReservationOperationResult> ExpandAsync(
        Guid leaseId,
        Guid projectId,
        long fencingToken,
        string sessionId,
        IReadOnlyList<ReservationScopeSpec> scopes,
        CancellationToken cancellationToken)
        => InvokeAsync(_transport.ExpandReservationAsync(
            new ExpandReservationMessage(
                leaseId, fencingToken, sessionId, ToScopes(scopes)),
            cancellationToken));

    public Task<ReservationOperationResult> ReleaseAsync(
        Guid leaseId,
        Guid projectId,
        string sessionId,
        CancellationToken cancellationToken)
        => InvokeAsync(_transport.ReleaseReservationAsync(
            new ReleaseReservationMessage(leaseId, sessionId), cancellationToken));

    public Task<ReservationOperationResult> TransferAsync(
        Guid leaseId,
        string fromSessionId,
        string toSessionId,
        CancellationToken cancellationToken)
        => InvokeAsync(_transport.TransferReservationAsync(
            new TransferReservationMessage(leaseId, fromSessionId, toSessionId), cancellationToken));

    public async Task<ReservationOperationResult> RenewAsync(
        Guid leaseId,
        long fencingToken,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var result = await _transport.RenewReservationAsync(
            new ReservationMutationMessage(leaseId, fencingToken, sessionId), cancellationToken)
            .ConfigureAwait(false);
        return new ReservationOperationResult(
            result.Lease is null ? null : ToLease(result.Lease),
            result.Error is null ? null : new GatewayError(result.Error.Code, result.Error.Message));
    }

    public async Task<MutationAuthorizationResult> AuthorizeAsync(
        Guid leaseId,
        long fencingToken,
        string sessionId,
        string targetPath,
        string operation,
        CancellationToken cancellationToken)
    {
        var (code, name) = ToOperation(operation);
        var result = await _transport.AuthorizeMutationAsync(
            new MutationAuthorizationMessage(
                leaseId, fencingToken, sessionId, targetPath, code, name),
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
        var leases = await _transport.ListReservationsAsync(
            new ListReservationsMessage(projectId, includeReleased),
            cancellationToken).ConfigureAwait(false);
        return [.. leases.Select(ToLease)];
    }

    public Task<ReservationOperationResult> MarkRecoveryRequiredAsync(
        Guid leaseId,
        string reason,
        CancellationToken cancellationToken)
        => InvokeAsync(_transport.MarkRecoveryRequiredAsync(
            new MarkRecoveryMessage(leaseId, reason), cancellationToken));

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

    private static async Task<ReservationOperationResult> InvokeAsync(
        Task<ReservationOperationResultMessage> call)
    {
        var result = await call.ConfigureAwait(false);
        return new ReservationOperationResult(
            result.Lease is null ? null : ToLease(result.Lease),
            result.Error is null ? null : new GatewayError(result.Error.Code, result.Error.Message));
    }

    private static ReservationLeaseInfo ToLease(ReservationLeaseMessage lease)
        => new(
            lease.LeaseId,
            lease.FencingToken,
            lease.StateName,
            lease.ExpiresAt,
            [.. lease.Scopes.Select(s => new ReservationScopeSpec(s.KindName, s.Path))],
            lease.OwnerSessionId);
}
