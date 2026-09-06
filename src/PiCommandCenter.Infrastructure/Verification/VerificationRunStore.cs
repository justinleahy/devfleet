using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Verification;

public sealed class VerificationRunStore(ControlPlaneDbContext db, IProjectionNotifier notifier)
    : IVerificationRunStore
{
    public async Task<VerificationRunDto> RecordAsync(
        VerificationRunDto run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var domain = VerificationRun.Record(
            new WorkRequestId(run.RequestId),
            run.ProfileId,
            run.CommandId,
            run.Status,
            run.ExitCode,
            run.StartedAt,
            run.CompletedAt,
            run.OutputSummary,
            run.OutputArtifactPath,
            run.Mandatory,
            run.Fingerprint,
            run.PolicyRevision,
            run.RunKind,
            run.AttemptId,
            run.Id);

        if (IsFinalKind(domain.RunKind))
        {
            var existing = await FindFinalAsync(domain, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return ToDto(existing);
            }
        }

        var row = new VerificationRunRow
        {
            Id = domain.Id,
            RequestId = domain.RequestId.Value,
            ProfileId = domain.ProfileId,
            CommandId = domain.CommandId,
            Status = domain.Status.ToString(),
            ExitCode = domain.ExitCode,
            StartedAtUtcTicks = domain.StartedAt.UtcTicks,
            CompletedAtUtcTicks = domain.CompletedAt?.UtcTicks,
            OutputSummary = domain.OutputSummary,
            OutputArtifactPath = domain.OutputArtifactPath,
            Mandatory = domain.Mandatory,
            Fingerprint = domain.Fingerprint,
            PolicyRevision = domain.PolicyRevision,
            RunKind = domain.RunKind.ToString(),
            AttemptId = domain.AttemptId,
        };

        db.VerificationRuns.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (IsFinalKind(domain.RunKind))
        {
            db.Entry(row).State = EntityState.Detached;
            var raced = await FindFinalAsync(domain, cancellationToken).ConfigureAwait(false);
            if (raced is null)
            {
                throw;
            }

            return ToDto(raced);
        }

        notifier.Publish(ProjectionChange.Request(Guid.Empty, row.RequestId));
        return ToDto(row);
    }

    public async Task<IReadOnlyList<VerificationRunDto>> ListAsync(
        WorkRequestId requestId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.VerificationRuns
            .AsNoTracking()
            .Where(r => r.RequestId == requestId.Value)
            .OrderBy(r => r.StartedAtUtcTicks)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<VerificationRunDto>> ListRecentAsync(
        WorkRequestId requestId,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        var baselineKind = VerificationRunKind.Baseline.ToString();
        var projectKind = VerificationRunKind.ProjectCheck.ToString();
        var intermediateKind = VerificationRunKind.Intermediate.ToString();
        var rows = await db.VerificationRuns
            .AsNoTracking()
            .Where(r => r.RequestId == requestId.Value
                && (r.RunKind == baselineKind
                    || r.RunKind == projectKind
                    || r.RunKind == intermediateKind))
            .OrderByDescending(r => r.StartedAtUtcTicks)
            .ThenByDescending(r => r.Id)
            .Take(maxCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(ToDto).ToList();
    }

    private Task<VerificationRunRow?> FindFinalAsync(
        VerificationRun domain,
        CancellationToken cancellationToken) =>
        db.VerificationRuns
            .AsNoTracking()
            .Where(r =>
                r.RequestId == domain.RequestId.Value
                && r.Fingerprint == domain.Fingerprint
                && r.PolicyRevision == domain.PolicyRevision
                && r.ProfileId == domain.ProfileId
                && r.CommandId == domain.CommandId
                && r.RunKind == domain.RunKind.ToString())
            .OrderBy(r => r.StartedAtUtcTicks)
            .FirstOrDefaultAsync(cancellationToken);

    private static bool IsFinalKind(VerificationRunKind kind) =>
        kind is VerificationRunKind.Baseline or VerificationRunKind.ProjectCheck;

    private static VerificationRunDto ToDto(VerificationRunRow row) => new(
        row.Id,
        row.RequestId,
        row.ProfileId,
        row.CommandId,
        Enum.TryParse<VerificationRunStatus>(row.Status, ignoreCase: true, out var status)
            ? status
            : VerificationRunStatus.Failed,
        row.ExitCode,
        new DateTimeOffset(row.StartedAtUtcTicks, TimeSpan.Zero),
        row.CompletedAtUtcTicks is { } done ? new DateTimeOffset(done, TimeSpan.Zero) : null,
        row.OutputSummary,
        row.OutputArtifactPath,
        row.Mandatory,
        row.Fingerprint,
        row.PolicyRevision,
        Enum.TryParse<VerificationRunKind>(row.RunKind, ignoreCase: true, out var kind)
            ? kind
            : throw new InvalidOperationException($"Unknown verification run kind '{row.RunKind}'."),
        row.AttemptId);
}
