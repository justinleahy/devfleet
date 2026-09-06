using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Node.Verification;

/// <summary>
/// Builds a bounded verification-policy catalog from trusted <see cref="VerificationOptions"/> only.
/// Invalid, duplicate, or overlong entries are omitted (fail closed). Never exposes executables,
/// argv, environment, credentials, raw config paths, or command output.
/// </summary>
public sealed class VerificationPolicyCatalogProvider : IVerificationPolicyCatalog
{
    public const int MaxTextLength = 128;
    public const int MaxProfileCount = 32;
    public const int MaxCommandCount = 32;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 86_400;

    private readonly IOptions<VerificationOptions> _options;
    private readonly TimeProvider _timeProvider;

    public VerificationPolicyCatalogProvider(
        IOptions<VerificationOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options;
        _timeProvider = timeProvider;
    }

    public VerificationPolicyCatalogMessage Capture()
    {
        var observedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        var profiles = BuildProfiles(_options.Value.Profiles);
        return new VerificationPolicyCatalogMessage(
            observedAt,
            BaselineAvailable: true,
            VerificationBaselineIds.Version,
            profiles);
    }

    /// <summary>
    /// Effective revision is the explicit configured revision when present, otherwise a
    /// deterministic hash of execution-affecting trusted fields plus safe display metadata.
    /// Only this opaque token is transported; never executables, argv, or raw paths.
    /// </summary>
    public static string EffectiveRevision(
        string profileId,
        string configKey,
        VerificationProfileOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(configKey);
        ArgumentNullException.ThrowIfNull(options);

        var explicitRevision = FirstBoundedText(options.Revision, null);
        return explicitRevision ?? ComputeFallbackRevision(profileId, configKey, options);
    }

    internal static bool IsAssignmentRepresentable(
        string profileId,
        string revision,
        IReadOnlyList<VerificationPolicyCommandMessage> commands)
    {
        var policyRevision = $"baseline:{VerificationBaselineIds.Version}+{profileId}@{revision}";
        if (policyRevision.Length is 0 or > ExecutionAssignment.MaxVerificationPolicyRevisionLength)
        {
            return false;
        }

        var extras = commands
            .Where(command => command.Mandatory)
            .Select(command => command.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Where(id => !string.Equals(id, IBaselineVerification.RepositoryIntegrityCommandId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var ordered = new string[extras.Length + 1];
        ordered[0] = IBaselineVerification.RepositoryIntegrityCommandId;
        extras.CopyTo(ordered, 1);
        var json = JsonSerializer.Serialize(ordered);
        return json.Length <= ExecutionAssignment.MaxMandatoryCommandIdsJsonLength;
    }



    public VerificationProfileSelectionResultMessage ValidateSelection(
        VerificationProfileSelectionRequestMessage request)
    {
        if (request is null
            || request.ProjectId == Guid.Empty
            || request.WorkspaceBindingId == Guid.Empty
            || request.WorkspaceBindingRevision < 0)
        {
            return Reject(VerificationPolicySelectionCodes.Malformed, "Selection request is malformed.", request);
        }

        var profileId = NormalizeOptional(request.ProfileId);
        var profileRevision = NormalizeOptional(request.ProfileRevision);
        if (profileId is null && profileRevision is null)
        {
            return new VerificationProfileSelectionResultMessage(
                Accepted: true,
                VerificationPolicySelectionCodes.Cleared,
                "Baseline-only verification is selected.",
                ProfileId: null,
                ProfileRevision: null);
        }

        if (profileId is null || profileRevision is null
            || !IsBoundedText(profileId)
            || !IsBoundedText(profileRevision))
        {
            return Reject(VerificationPolicySelectionCodes.Malformed, "Profile selection is malformed.", request);
        }

        var catalog = Capture();
        VerificationPolicyProfileMessage? match = null;
        foreach (var profile in catalog.Profiles)
        {
            if (string.Equals(profile.Id, profileId, StringComparison.Ordinal))
            {
                match = profile;
                break;
            }
        }

        if (match is null)
        {
            return new VerificationProfileSelectionResultMessage(
                false,
                VerificationPolicySelectionCodes.Missing,
                "Selected verification profile is not advertised by this node.",
                profileId,
                profileRevision);
        }

        if (!string.Equals(match.Revision, profileRevision, StringComparison.Ordinal))
        {
            return new VerificationProfileSelectionResultMessage(
                false,
                VerificationPolicySelectionCodes.Stale,
                "Selected verification profile revision does not match the live catalog.",
                profileId,
                profileRevision);
        }
        if (!IsAssignmentRepresentable(match.Id, match.Revision, match.Commands))
        {
            return new VerificationProfileSelectionResultMessage(
                false,
                VerificationPolicySelectionCodes.Missing,
                "Selected verification profile is not advertised by this node.",
                profileId,
                profileRevision);
        }

        return new VerificationProfileSelectionResultMessage(
            true,
            VerificationPolicySelectionCodes.Accepted,
            "Selected verification profile matches the live catalog.",
            match.Id,
            match.Revision);
    }

    private static IReadOnlyList<VerificationPolicyProfileMessage> BuildProfiles(
        Dictionary<string, VerificationProfileOptions> configured)
    {
        var profiles = new List<VerificationPolicyProfileMessage>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, options) in configured)
        {
            if (profiles.Count >= MaxProfileCount)
            {
                break;
            }

            if (!TryProjectProfile(key, options, seenIds, out var profile))
            {
                continue;
            }

            profiles.Add(profile);
        }

        profiles.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        return profiles;
    }

    private static bool TryProjectProfile(
        string key,
        VerificationProfileOptions? options,
        HashSet<string> seenIds,
        out VerificationPolicyProfileMessage profile)
    {
        profile = null!;
        if (options is null)
        {
            return false;
        }

        if (options.Commands.Any(command =>
                command is not null
                && VerificationBaselineIds.IsReservedCommandId(command.Id)))
        {
            return false;
        }

        var id = FirstBoundedText(options.Id, key);
        if (id is null
            || string.Equals(id, VerificationBaselineIds.ProfileId, StringComparison.Ordinal)
            || !seenIds.Add(id))
        {
            return false;
        }

        var commands = ProjectCommands(options.Commands);
        if (commands.Count == 0)
        {
            return false;
        }

        var displayLabel = FirstBoundedText(options.DisplayLabel, id);
        if (displayLabel is null)
        {
            return false;
        }

        var revision = EffectiveRevision(id, key, options);
        if (!IsAssignmentRepresentable(id, revision, commands))
        {
            return false;
        }

        profile = new VerificationPolicyProfileMessage(id, revision, displayLabel, commands);
        return true;
    }

    private static IReadOnlyList<VerificationPolicyCommandMessage> ProjectCommands(
        List<VerificationCommandOptions> commands)
    {
        var projected = new List<VerificationPolicyCommandMessage>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            if (projected.Count >= MaxCommandCount)
            {
                break;
            }

            if (command is null)
            {
                continue;
            }

            var id = FirstBoundedText(command.Id, null);
            if (id is null
                || VerificationBaselineIds.IsReservedCommandId(id)
                || !seenIds.Add(id))
            {
                continue;
            }

            var displayLabel = FirstBoundedText(command.DisplayLabel, id);
            var workingDirectoryLabel = FirstBoundedText(
                command.WorkingDirectoryLabel,
                DeriveWorkingDirectoryLabel(command.WorkingDirectory));
            if (displayLabel is null
                || workingDirectoryLabel is null
                || command.TimeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
            {
                continue;
            }

            projected.Add(new VerificationPolicyCommandMessage(
                id,
                displayLabel,
                workingDirectoryLabel,
                command.Mandatory,
                command.TimeoutSeconds));
        }

        return projected;
    }

    private static string DeriveWorkingDirectoryLabel(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return ".";
        }

        var trimmed = workingDirectory.Trim();
        if (trimmed.Contains('\\', StringComparison.Ordinal)
            || trimmed.StartsWith('/')
            || trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.Length > MaxTextLength)
        {
            return ".";
        }

        return trimmed;
    }

    private static string ComputeFallbackRevision(
        string profileId,
        string configKey,
        VerificationProfileOptions options)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", profileId);
            writer.WriteString("key", configKey);
            writer.WriteString("label", options.DisplayLabel ?? string.Empty);
            writer.WriteStartArray("commands");
            foreach (var command in options.Commands)
            {
                if (command is null)
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("id", command.Id ?? string.Empty);
                writer.WriteString("label", command.DisplayLabel ?? string.Empty);
                writer.WriteString("executable", command.Executable ?? string.Empty);
                writer.WriteStartArray("argv");
                foreach (var argument in command.Arguments ?? [])
                {
                    writer.WriteStringValue(argument ?? string.Empty);
                }

                writer.WriteEndArray();
                writer.WriteString("cwd", NormalizeWorkingDirectory(command.WorkingDirectory));
                writer.WriteString("cwdLabel", command.WorkingDirectoryLabel ?? string.Empty);
                writer.WriteBoolean("mandatory", command.Mandatory);
                writer.WriteNumber("timeout", command.TimeoutSeconds);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }

    private static string NormalizeWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return ".";
        }

        var trimmed = workingDirectory.Trim().Replace('\\', '/');
        return trimmed.Length == 0 ? "." : trimmed;
    }


    private static VerificationProfileSelectionResultMessage Reject(
        string code,
        string detail,
        VerificationProfileSelectionRequestMessage? request) =>
        new(
            false,
            code,
            detail,
            NormalizeOptional(request?.ProfileId),
            NormalizeOptional(request?.ProfileRevision));

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? FirstBoundedText(string? preferred, string? fallback)
    {
        var candidate = NormalizeOptional(preferred) ?? NormalizeOptional(fallback);
        return IsBoundedText(candidate) ? candidate : null;
    }

    internal static bool IsBoundedText(string? value) =>
        value is { Length: > 0 and <= MaxTextLength }
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1]);
}

/// <summary>Node-local verification policy catalog and selection validation.</summary>
public interface IVerificationPolicyCatalog
{
    VerificationPolicyCatalogMessage Capture();

    VerificationProfileSelectionResultMessage ValidateSelection(
        VerificationProfileSelectionRequestMessage request);
}
