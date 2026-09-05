using Microsoft.Extensions.Options;
using PiCommandCenter.Node.Child;

namespace PiCommandCenter.Node.Verification;

/// <summary>
/// Typed configured command runner (SPEC §20): trusted profiles only, exclusive
/// <c>project-build</c> lease, no active source mutation, argument-list process,
/// bounded capture, always-release.
/// </summary>
public sealed class VerificationCommandRunner : IVerificationCommandRunner
{
    private readonly IOptions<VerificationOptions> _options;
    private readonly INodeReservationGateway _reservations;

    public VerificationCommandRunner(
        IOptions<VerificationOptions> options,
        INodeReservationGateway reservations)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
    }

    public async Task<VerificationProfileRunResult> RunAsync(
        VerificationRunContext context,
        string profileId,
        string? commandId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.RepositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.OwnerSessionId);

        var options = _options.Value;
        if (!TryGetProfile(options, profileId, out var profile))
        {
            throw new VerificationRejectedException(
                "unknown_profile",
                $"Verification profile '{profileId}' is not in trusted node configuration.");
        }

        var commands = profile.Commands
            .Where(c => commandId is null || string.Equals(c.Id, commandId, StringComparison.Ordinal))
            .ToList();
        if (commands.Count == 0)
        {
            throw new VerificationRejectedException(
                "unknown_command",
                commandId is null
                    ? $"Verification profile '{profileId}' has no commands."
                    : $"Verification command '{commandId}' is not in trusted profile '{profileId}'.");
        }

        await RejectActiveSourceMutationAsync(context.ProjectId, cancellationToken).ConfigureAwait(false);

        var acquire = await _reservations.AcquireAsync(
            context.ProjectId,
            context.RequestId,
            context.OwnerSessionId,
            [new ReservationScopeSpec("resource", VerificationOptions.ProjectBuildResource)],
            "verification",
            cancellationToken).ConfigureAwait(false);

        if (!acquire.Ok || acquire.Lease is null)
        {
            throw new VerificationRejectedException(
                acquire.Error?.Code ?? "build_lease_denied",
                acquire.Error?.Message ?? "Failed to acquire project-build.");
        }

        var lease = acquire.Lease;
        try
        {
            await RejectActiveSourceMutationAsync(context.ProjectId, cancellationToken).ConfigureAwait(false);

            var results = new List<VerificationCommandResult>(commands.Count);
            foreach (var command in commands)
            {
                results.Add(await RunCommandAsync(context, command, options.MaxOutputBytes, cancellationToken)
                    .ConfigureAwait(false));
            }

            var succeeded = results.All(r =>
                !r.Mandatory
                || (!r.TimedOut && !r.Cancelled && !r.Crashed && r.ExitCode == 0));
            return new VerificationProfileRunResult(profile.Id, results, succeeded);
        }
        finally
        {
            try
            {
                await _reservations.ReleaseAsync(
                    lease.LeaseId,
                    context.ProjectId,
                    context.OwnerSessionId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // lease release is best-effort after the run; never hide the command result
            }
        }
    }

    private static bool TryGetProfile(
        VerificationOptions options,
        string profileId,
        out VerificationProfileOptions profile)
    {
        foreach (var (key, candidate) in options.Profiles)
        {
            var id = string.IsNullOrWhiteSpace(candidate.Id) ? key : candidate.Id;
            if (string.Equals(id, profileId, StringComparison.Ordinal))
            {
                profile = candidate;
                if (string.IsNullOrWhiteSpace(profile.Id))
                {
                    profile.Id = key;
                }

                return true;
            }
        }

        profile = null!;
        return false;
    }

    private async Task RejectActiveSourceMutationAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var leases = await _reservations.ListAsync(projectId, includeReleased: false, cancellationToken)
            .ConfigureAwait(false);
        foreach (var lease in leases)
        {
            if (!string.Equals(lease.State, "Active", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (lease.Scopes.Any(IsSourceMutationScope))
            {
                throw new VerificationRejectedException(
                    "active_source_mutation",
                    "Final verification is incompatible with an active source mutation lease.");
            }
        }
    }

    private static bool IsSourceMutationScope(ReservationScopeSpec scope)
    {
        var kind = scope.Kind;
        return kind.Equals("file", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("directory", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("File", StringComparison.Ordinal)
            || kind.Equals("Directory", StringComparison.Ordinal);
    }

    private static async Task<VerificationCommandResult> RunCommandAsync(
        VerificationRunContext context,
        VerificationCommandOptions command,
        int maxOutputBytes,
        CancellationToken cancellationToken)
    {
        var workingDirectory = ResolveWorkingDirectory(context.RepositoryRoot, command.WorkingDirectory);
        var timeout = TimeSpan.FromSeconds(command.TimeoutSeconds);
        var run = await BoundedProcessRunner.RunAsync(
            command.Executable,
            command.Arguments,
            workingDirectory,
            maxOutputBytes,
            timeout,
            cancellationToken,
            sandboxRepositoryRoot: context.RepositoryRoot).ConfigureAwait(false);

        return new VerificationCommandResult(
            command.Id,
            command.Executable,
            command.Arguments,
            workingDirectory,
            run.ExitCode,
            run.Duration,
            run.StandardOutput,
            run.StandardError,
            run.TimedOut,
            run.Cancelled,
            run.Crashed,
            run.OutputTruncated,
            ArtifactPath: null,
            command.Mandatory);
    }

    internal static string ResolveWorkingDirectory(string repositoryRoot, string relative)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var combined = Path.GetFullPath(Path.Combine(root, relative));
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combined, root, StringComparison.Ordinal))
        {
            throw new VerificationRejectedException(
                "working_directory_escape",
                "Verification working directory must stay inside the canonical repository.");
        }

        return combined;
    }
}
