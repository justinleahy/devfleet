using System.Text.Json;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Infrastructure.Requests;

/// <summary>
/// Derives the immutable assignment verification-policy snapshot from a Project selection
/// and the node-advertised catalog. Never executes commands or exposes process details.
/// </summary>
internal static class VerificationPolicyAssignmentCapture
{
    public const string BaselineId = "devfleet-baseline";
    public const string BuiltInBaselineVersion = "1";
    public const string RepositoryIntegrityCommandId = "repository-integrity";

    private const int MaxCatalogProfiles = 64;
    private const int MaxCatalogCommands = 64;
    private const int MaxLabelLength = 128;

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryCreate(
        string? selectedProfileId,
        string? selectedProfileRevision,
        NodeExecutionStatusDto? executionStatus,
        bool requireFreshCatalog,
        DateTimeOffset now,
        TimeSpan staleAfter,
        out string policyRevision,
        out string baselineVersion,
        out string? profileId,
        out string? profileRevision,
        out string mandatoryCommandIdsJson)
    {
        policyRevision = string.Empty;
        baselineVersion = string.Empty;
        profileId = null;
        profileRevision = null;
        mandatoryCommandIdsJson = string.Empty;

        var hasSelection = !string.IsNullOrWhiteSpace(selectedProfileId)
            || !string.IsNullOrWhiteSpace(selectedProfileRevision);
        if (hasSelection
            && (string.IsNullOrWhiteSpace(selectedProfileId)
                || string.IsNullOrWhiteSpace(selectedProfileRevision)))
        {
            return false;
        }

        var catalog = executionStatus?.VerificationPolicy;
        var catalogValid = IsCatalogValid(catalog);
        var catalogFresh = catalogValid
            && catalog is not null
            && IsFreshUtc(catalog.ObservedAt, now, staleAfter);

        if (hasSelection)
        {
            if (!requireFreshCatalog)
            {
                // Claim/reconcile still fail closed on a missing or stale advertised catalog.
            }

            if (!catalogFresh
                || catalog is null
                || !catalog.BaselineAvailable
                || !string.Equals(catalog.BaselineVersion, BuiltInBaselineVersion, StringComparison.Ordinal))
            {
                return false;
            }

            var profile = catalog.Profiles.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, selectedProfileId!.Trim(), StringComparison.Ordinal)
                && string.Equals(candidate.Revision, selectedProfileRevision!.Trim(), StringComparison.Ordinal));
            if (profile is null)
            {
                return false;
            }

            return TryCompose(
                catalog.BaselineVersion,
                profile.Id,
                profile.Revision,
                profile.Commands.Where(command => command.Mandatory).Select(command => command.Id),
                out policyRevision,
                out baselineVersion,
                out profileId,
                out profileRevision,
                out mandatoryCommandIdsJson);
        }

        var version = catalogFresh && catalog is not null && catalog.BaselineAvailable
            ? catalog.BaselineVersion
            : BuiltInBaselineVersion;
        return TryCompose(
            version,
            profileId: null,
            profileRevision: null,
            extraMandatoryIds: [],
            out policyRevision,
            out baselineVersion,
            out profileId,
            out profileRevision,
            out mandatoryCommandIdsJson);
    }

    public static bool IsSelectedProfileAdvertised(
        string profileId,
        string profileRevision,
        NodeExecutionStatusDto executionStatus,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        return TryCreate(
            profileId,
            profileRevision,
            executionStatus,
            requireFreshCatalog: true,
            now,
            staleAfter,
            out _,
            out _,
            out _,
            out _,
            out _);
    }

    public static NodeExecutionStatusDto? DeserializeExecutionStatus(string? json, int maxJsonLength)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > maxJsonLength)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<NodeExecutionStatusDto>(json, SnapshotJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static string SerializeMandatoryCommandIds(IEnumerable<string> commandIds)
    {
        // Display order keeps repository-integrity first; extras are distinct ordinal-sorted.
        var ordered = DistinctSortedMandatoryIds(commandIds);
        return JsonSerializer.Serialize(ordered);
    }

    private static bool TryCompose(
        string baselineVersion,
        string? profileId,
        string? profileRevision,
        IEnumerable<string> extraMandatoryIds,
        out string policyRevision,
        out string capturedBaseline,
        out string? capturedProfileId,
        out string? capturedProfileRevision,
        out string mandatoryCommandIdsJson)
    {
        policyRevision = string.Empty;
        capturedBaseline = string.Empty;
        capturedProfileId = null;
        capturedProfileRevision = null;
        mandatoryCommandIdsJson = string.Empty;

        if (!IsBoundedToken(baselineVersion, ExecutionAssignment.MaxBaselineVersionLength))
        {
            return false;
        }

        var revision = profileId is null
            ? $"baseline:{baselineVersion}"
            : $"baseline:{baselineVersion}+{profileId}@{profileRevision}";
        if (!IsBoundedToken(revision, ExecutionAssignment.MaxVerificationPolicyRevisionLength))
        {
            return false;
        }

        if (profileId is not null)
        {
            if (!IsBoundedToken(profileId, ExecutionAssignment.MaxTrustedVerificationProfileIdLength)
                || !IsBoundedToken(profileRevision, ExecutionAssignment.MaxTrustedVerificationProfileRevisionLength))
            {
                return false;
            }
        }

        var json = SerializeMandatoryCommandIds(extraMandatoryIds);
        if (json.Length > ExecutionAssignment.MaxMandatoryCommandIdsJsonLength)
        {
            return false;
        }

        policyRevision = revision;
        capturedBaseline = baselineVersion;
        capturedProfileId = profileId;
        capturedProfileRevision = profileRevision;
        mandatoryCommandIdsJson = json;
        return true;
    }

    private static string[] DistinctSortedMandatoryIds(IEnumerable<string> extraMandatoryIds)
    {
        var extras = extraMandatoryIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Where(id => !string.Equals(id, RepositoryIntegrityCommandId, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var ordered = new string[extras.Length + 1];
        ordered[0] = RepositoryIntegrityCommandId;
        extras.CopyTo(ordered, 1);
        return ordered;
    }

    private static bool IsProfileAssignmentRepresentable(VerificationPolicyProfileMessage profile)
    {
        var policyRevision = $"baseline:{BuiltInBaselineVersion}+{profile.Id}@{profile.Revision}";
        if (!IsBoundedToken(policyRevision, ExecutionAssignment.MaxVerificationPolicyRevisionLength))
        {
            return false;
        }

        var json = SerializeMandatoryCommandIds(
            profile.Commands.Where(command => command.Mandatory).Select(command => command.Id));
        return json.Length <= ExecutionAssignment.MaxMandatoryCommandIdsJsonLength;
    }


    internal static bool IsCatalogValid(VerificationPolicyCatalogMessage? catalog)
    {
        if (catalog is null
            || catalog.ObservedAt.Offset != TimeSpan.Zero
            || !IsBoundedToken(catalog.BaselineVersion, ExecutionAssignment.MaxBaselineVersionLength)
            || catalog.Profiles is null
            || catalog.Profiles.Count > MaxCatalogProfiles)
        {
            return false;
        }

        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in catalog.Profiles)
        {
            if (profile is null
                || !IsBoundedToken(profile.Id, ExecutionAssignment.MaxTrustedVerificationProfileIdLength)
                || !IsBoundedToken(profile.Revision, ExecutionAssignment.MaxTrustedVerificationProfileRevisionLength)
                || !IsBoundedLabel(profile.DisplayLabel)
                || profile.Commands is null
                || profile.Commands.Count > MaxCatalogCommands
                || !profileIds.Add(profile.Id)
                || !IsProfileAssignmentRepresentable(profile))
            {
                return false;
            }

            var commandIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var command in profile.Commands)
            {
                if (command is null
                    || !IsBoundedToken(command.Id, MaxLabelLength)
                    || !IsBoundedLabel(command.DisplayLabel)
                    || !IsBoundedLabel(command.WorkingDirectoryLabel)
                    || command.TimeoutSeconds < 0
                    || !commandIds.Add(command.Id))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsFreshUtc(DateTimeOffset observedAt, DateTimeOffset now, TimeSpan staleAfter) =>
        observedAt.Offset == TimeSpan.Zero
        && observedAt <= now
        && now - observedAt <= staleAfter;

    private static bool IsBoundedToken(string? value, int maxLength) =>
        value is { Length: > 0 }
        && value.Length <= maxLength
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1])
        && !value.Any(char.IsControl);

    private static bool IsBoundedLabel(string? value) =>
        value is { Length: > 0 and <= MaxLabelLength }
        && !value.Any(char.IsControl);
}
