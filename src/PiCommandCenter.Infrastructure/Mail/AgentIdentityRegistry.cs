using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Domain;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Mail;

/// <summary>
/// EF Core implementation of <see cref="IAgentIdentityRegistry"/>. Uniqueness of (ProjectId,
/// AgentName) among active identities is enforced by a database unique index, so concurrent
/// allocations cannot both win; the loser catches the index violation and retries with the
/// next deterministic {@code name-N} suffix.
/// </summary>
public sealed class AgentIdentityRegistry(TimeProvider clock, ControlPlaneDbContext db) : IAgentIdentityRegistry
{
    private const int MaxNameLength = 128;
    private const int MaxRetries = 8;

    public async Task<AgentIdentityDto> AllocateAsync(AllocateAgentIdentityCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);

        var existing = await db.Set<MailAgentIdentityRow>()
            .SingleOrDefaultAsync(i => i.SessionId == command.SessionId, cancellationToken);
        if (existing is not null)
        {
            if (existing.ProjectId != command.ProjectId.Value)
            {
                throw new MailValidationException($"Session '{command.SessionId}' already holds an identity in a different project.");
            }

            return ToDto(existing);
        }

        var now = clock.GetUtcNow().UtcTicks;
        var baseName = command.RequestedName.Trim();
        for (var attempt = 0; ; attempt++)
        {
            var candidate = attempt == 0
                ? baseName
                : $"{baseName}-{attempt + 1}";
            var row = new MailAgentIdentityRow
            {
                SessionId = command.SessionId,
                ProjectId = command.ProjectId.Value,
                AgentName = candidate,
                Role = command.Role.Trim(),
                Runtime = command.Runtime.Trim(),
                AllocatedAtUtcTicks = now,
            };
            db.Set<MailAgentIdentityRow>().Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return ToDto(row);
            }
            catch (DbUpdateException) when (attempt + 1 < MaxRetries)
            {
                // Lost a concurrent insert: either the name was claimed by another session
                // (unique index) or the session was registered concurrently (primary key).
                // Detach and re-run the deterministic scan; if the session now holds an
                // identity, return it instead of inventing a suffixed duplicate.
                db.Entry(row).State = EntityState.Detached;
                db.ChangeTracker.Clear();
                existing = await db.Set<MailAgentIdentityRow>()
                    .SingleOrDefaultAsync(i => i.SessionId == command.SessionId, cancellationToken);
                if (existing is not null)
                {
                    return ToDto(existing);
                }
            }
        }
    }

    public async Task ReleaseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new MailValidationException("SessionId must not be empty.");
        }

        var row = await db.Set<MailAgentIdentityRow>()
            .SingleOrDefaultAsync(i => i.SessionId == sessionId, cancellationToken);
        if (row is not null)
        {
            db.Set<MailAgentIdentityRow>().Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<AgentIdentityDto?> FindByNameAsync(ProjectId projectId, string agentName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentName))
        {
            throw new MailValidationException("AgentName must not be empty.");
        }

        var row = await db.Set<MailAgentIdentityRow>()
            .SingleOrDefaultAsync(
                i => i.ProjectId == projectId.Value && i.AgentName == agentName.Trim(),
                cancellationToken);
        return row is null ? null : ToDto(row);
    }

    private static void Validate(AllocateAgentIdentityCommand command)
    {
        ValidateField(command.SessionId, "SessionId");
        ValidateField(command.RequestedName, "RequestedName");
        if (command.RequestedName.Trim().Length > MaxNameLength)
        {
            throw new MailValidationException($"RequestedName exceeds {MaxNameLength} characters.");
        }

        ValidateField(command.Role, "Role");
        ValidateField(command.Runtime, "Runtime");
    }

    private static void ValidateField(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MailValidationException($"{fieldName} must not be empty.");
        }
    }

    private static AgentIdentityDto ToDto(MailAgentIdentityRow row) => new(
        new ProjectId(row.ProjectId),
        row.SessionId,
        row.AgentName,
        row.Role,
        row.Runtime,
        new DateTimeOffset(row.AllocatedAtUtcTicks, TimeSpan.Zero));
}
