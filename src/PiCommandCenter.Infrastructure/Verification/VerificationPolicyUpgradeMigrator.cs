using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Verification;

/// <summary>
/// One-time upgrade evaluation for Projects designated to a heartbeat node: migrate
/// historical <c>default</c> runs only when selection is still null, and persist an
/// evaluation audit for every un-audited Project so later heartbeats never reconsider it.
/// </summary>
public sealed class VerificationPolicyUpgradeMigrator(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IProjectionNotifier notifier,
    ILogger<VerificationPolicyUpgradeMigrator> logger)
{
    public const string HistoricalDefaultProfileId = "default";
    public const string AuditReason = "historical default profile auto-selected";
    public const string AuditReasonNoHistory = "no historical default verification";
    public const string AuditReasonExplicitSelection = "explicit profile selection";
    public const string AuditReasonDefaultUnavailable =
        "historical default profile unavailable at first catalog heartbeat";

    public async Task MigrateAfterHeartbeatAsync(
        NodeId nodeId,
        VerificationPolicyCatalogMessage? catalog,
        CancellationToken cancellationToken = default)
    {
        if (catalog is null)
        {
            return;
        }

        var advertised = FindAdvertisedDefault(catalog);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var alreadyMigratedProjectIds = db.Set<VerificationPolicyUpgradeAuditRow>()
            .Select(row => row.ProjectId);

        var designated = await db.Projects
            .Where(project =>
                !alreadyMigratedProjectIds.Contains(project.Id)
                && db.WorkspaceBindings.Any(binding =>
                    binding.ProjectId == project.Id
                    && binding.NodeId == nodeId))
            .ToListAsync(cancellationToken);

        if (designated.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var designatedProjectIds = designated.Select(project => project.Id).ToArray();
        var requestGuidsByProject = await db.WorkRequests
            .Where(request => designatedProjectIds.Contains(request.ProjectId))
            .Select(request => new { RequestGuid = request.Id.Value, request.ProjectId })
            .ToListAsync(cancellationToken);

        var requestGuids = requestGuidsByProject.Select(row => row.RequestGuid).ToArray();
        var historyRequestGuids = requestGuids.Length == 0
            ? []
            : await db.VerificationRuns
                .Where(run =>
                    requestGuids.Contains(run.RequestId)
                    && run.ProfileId == HistoricalDefaultProfileId)
                .Select(run => run.RequestId)
                .ToListAsync(cancellationToken);

        var historyProjectIds = requestGuidsByProject
            .Where(row => historyRequestGuids.Contains(row.RequestGuid))
            .Select(row => row.ProjectId)
            .ToHashSet();

        var now = clock.GetUtcNow();
        var migrated = new List<Project>();
        foreach (var project in designated)
        {
            var hasNullSelection = project.TrustedVerificationProfileId is null
                && project.TrustedVerificationProfileRevision is null;
            var migrate = advertised is not null && hasNullSelection && historyProjectIds.Contains(project.Id);
            string reason;
            string profileId;
            string profileRevision;
            if (migrate)
            {
                project.SelectTrustedVerificationProfile(
                    advertised!.Id,
                    advertised.Revision,
                    now);
                reason = AuditReason;
                profileId = advertised.Id;
                profileRevision = advertised.Revision;
                migrated.Add(project);
            }
            else if (!hasNullSelection)
            {
                reason = AuditReasonExplicitSelection;
                profileId = project.TrustedVerificationProfileId ?? string.Empty;
                profileRevision = project.TrustedVerificationProfileRevision ?? string.Empty;
            }
            else
            {
                reason = historyProjectIds.Contains(project.Id)
                    ? AuditReasonDefaultUnavailable
                    : AuditReasonNoHistory;
                profileId = string.Empty;
                profileRevision = string.Empty;
            }

            db.Set<VerificationPolicyUpgradeAuditRow>().Add(new VerificationPolicyUpgradeAuditRow
            {
                ProjectId = project.Id,
                ProfileId = Bound(profileId, VerificationPolicyUpgradeAuditRow.MaxProfileIdLength),
                ProfileRevision = Bound(
                    profileRevision,
                    VerificationPolicyUpgradeAuditRow.MaxProfileRevisionLength),
                Reason = Bound(reason, VerificationPolicyUpgradeAuditRow.MaxReasonLength),
                MigratedAtUtcTicks = now.UtcTicks,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        foreach (var project in migrated)
        {
            logger.LogInformation(
                "AUDIT verification-policy-default-migration project={ProjectId} profile={ProfileId} revision={ProfileRevision}",
                project.Id.Value,
                advertised!.Id,
                advertised.Revision);
        }

        await PublishEligibilityAsync(migrated.Select(project => project.Id).ToArray(), cancellationToken);
    }

    private static VerificationPolicyProfileMessage? FindAdvertisedDefault(
        VerificationPolicyCatalogMessage? catalog)
    {
        if (catalog?.Profiles is null)
        {
            return null;
        }

        foreach (var profile in catalog.Profiles)
        {
            if (profile is not null
                && string.Equals(profile.Id, HistoricalDefaultProfileId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(profile.Revision))
            {
                return profile;
            }
        }

        return null;
    }

    private async Task PublishEligibilityAsync(
        IReadOnlyList<ProjectId> projectIds,
        CancellationToken cancellationToken)
    {
        var queuedRequests = await db.WorkRequests
            .AsNoTracking()
            .Where(request => projectIds.Contains(request.ProjectId)
                && request.Status == WorkRequestStatus.Queued)
            .Select(request => new { request.ProjectId, RequestId = request.Id })
            .ToListAsync(cancellationToken);

        foreach (var projectId in projectIds)
        {
            notifier.Publish(ProjectionChange.Project(projectId.Value));
        }

        foreach (var request in queuedRequests)
        {
            notifier.Publish(ProjectionChange.Request(
                request.ProjectId.Value,
                request.RequestId.Value));
        }
    }

    private static string Bound(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
