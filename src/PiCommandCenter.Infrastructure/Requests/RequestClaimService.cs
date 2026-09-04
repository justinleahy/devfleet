using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Requests;

/// <summary>
/// Atomically claims queued work for a node. Claims are taken inside a Serializable
/// transaction that re-checks per-project capacity (one active Development request,
/// the configured read-only limit) and the deterministic queue order
/// (Priority descending, then CreatedAt ascending) at commit time, so concurrent
/// claimers can never both satisfy the same request or oversubscribe a project.
/// The unique primary key on <c>RequestClaims.RequestId</c> is the final backstop.
/// </summary>
public sealed class RequestClaimService(TimeProvider clock, ControlPlaneDbContext db) : IRequestClaimService
{
    /// <summary>Request statuses that occupy project concurrency slots.</summary>
    private static readonly WorkRequestStatus[] InFlightStatuses =
    [
        WorkRequestStatus.Starting,
        WorkRequestStatus.Planning,
        WorkRequestStatus.Executing,
        WorkRequestStatus.Reviewing,
        WorkRequestStatus.Verifying,
        WorkRequestStatus.Blocked,
    ];

    public async Task<RequestClaimDto?> ClaimNextAsync(
        NodeId nodeId,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);

        var now = clock.GetUtcNow();

        _ = await db.FleetNodes.AsNoTracking()
            .SingleOrDefaultAsync(n => n.Id == nodeId, cancellationToken)
            ?? throw new NodeNotFoundException(nodeId);

        var projects = await db.Projects
            .Where(p => p.NodeId == nodeId && p.Enabled)
            .ToListAsync(cancellationToken);
        if (projects.Count == 0)
        {
            return null;
        }

        var projectLimits = projects.ToDictionary(p => p.Id, p => p.MaxReadOnlyRequests);
        var projectIds = projects.Select(p => p.Id).ToList();

        var activeClaims = db.RequestClaims
            .Where(c => c.LeaseExpiresAt > now)
            .Join(db.WorkRequests,
                claim => claim.RequestId,
                request => request.Id,
                (claim, request) => new { claim.ProjectId, request.Kind, request.Status })
            .Where(x => InFlightStatuses.Contains(x.Status));

        var activeDevelopmentCounts = await activeClaims
            .Where(x => x.Kind == WorkRequestKind.Development)
            .GroupBy(x => x.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count, cancellationToken);

        var activeReadOnlyCounts = await activeClaims
            .Where(x => x.Kind != WorkRequestKind.Development)
            .GroupBy(x => x.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Count, cancellationToken);

        var candidates = await db.WorkRequests
            .Where(r => projectIds.Contains(r.ProjectId) && r.Status == WorkRequestStatus.Queued)
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            var isDevelopment = candidate.Kind == WorkRequestKind.Development;
            if (isDevelopment)
            {
                if (activeDevelopmentCounts.GetValueOrDefault(candidate.ProjectId) > 0)
                {
                    continue;
                }
            }
            else if (activeReadOnlyCounts.GetValueOrDefault(candidate.ProjectId)
                >= projectLimits[candidate.ProjectId])
            {
                continue;
            }

            var claimToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var claim = RequestClaim.Create(candidate.Id, candidate.ProjectId, nodeId, claimToken, now, lease);
            candidate.Start(now);

            db.RequestClaims.Add(claim);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new RequestClaimDto(
                claim.RequestId.Value,
                claim.ProjectId.Value,
                claim.NodeId.Value,
                claim.ClaimToken,
                claim.ClaimedAt,
                claim.LeaseExpiresAt);
        }

        return null;
    }

    public async Task<DateTimeOffset> RenewAsync(
        WorkRequestId requestId,
        NodeId nodeId,
        string claimToken,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();

        var claim = await db.RequestClaims
            .SingleOrDefaultAsync(c => c.RequestId == requestId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No active claim exists for request '{requestId}'.");

        // Domain validation rejects a wrong node, a wrong token, and an expired lease.
        var leaseExpiresAt = claim.Renew(nodeId, claimToken, lease, now);

        await db.SaveChangesAsync(cancellationToken);
        return leaseExpiresAt;
    }
}
