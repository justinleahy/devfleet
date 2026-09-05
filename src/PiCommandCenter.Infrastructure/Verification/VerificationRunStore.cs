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
            run.Id);

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
        };

        db.VerificationRuns.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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
        row.Mandatory);
}
