namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>Built-in baseline verification identifiers advertised by every node.</summary>
public static class VerificationBaselineIds
{
    public const string ProfileId = "devfleet-baseline";
    public const string Version = "1";
    public const string RepositoryIntegrityCommandId = "repository-integrity";
    public const string WhitespaceCommandId = "whitespace";

    public static bool IsReservedCommandId(string? commandId)
    {
        var normalized = commandId?.Trim();
        return string.Equals(normalized, RepositoryIntegrityCommandId, StringComparison.Ordinal)
            || string.Equals(normalized, WhitespaceCommandId, StringComparison.Ordinal);
    }
}

/// <summary>Stable selection-validation result codes. Never include command output.</summary>
public static class VerificationPolicySelectionCodes
{
    public const string Accepted = "accepted";
    public const string Cleared = "cleared";
    public const string Malformed = "malformed";
    public const string Missing = "missing";
    public const string Stale = "stale";
}

/// <summary>One trusted command advertised without executable, argv, environment, or output.</summary>
public sealed record VerificationPolicyCommandMessage(
    string Id,
    string DisplayLabel,
    string WorkingDirectoryLabel,
    bool Mandatory,
    int TimeoutSeconds);

/// <summary>One trusted profile from node configuration, excluding secrets and process details.</summary>
public sealed record VerificationPolicyProfileMessage(
    string Id,
    string Revision,
    string DisplayLabel,
    IReadOnlyList<VerificationPolicyCommandMessage> Commands);

/// <summary>
/// Bounded verification-policy catalog and readiness snapshot. Baseline is always advertised;
/// trusted profiles are optional and never include executables, argv, environment, credentials,
/// raw config paths, or command output.
/// </summary>
public sealed record VerificationPolicyCatalogMessage(
    DateTimeOffset ObservedAt,
    bool BaselineAvailable,
    string BaselineVersion,
    IReadOnlyList<VerificationPolicyProfileMessage> Profiles);

/// <summary>
/// Control-plane request to validate a Project's selected trusted profile against the live node catalog.
/// A null profile id and revision means baseline-only (clear selection).
/// </summary>
public sealed record VerificationProfileSelectionRequestMessage(
    Guid ProjectId,
    Guid WorkspaceBindingId,
    long WorkspaceBindingRevision,
    string? ProfileId,
    string? ProfileRevision);

/// <summary>Bounded selection validation result. Does not execute verification commands.</summary>
public sealed record VerificationProfileSelectionResultMessage(
    bool Accepted,
    string Code,
    string Detail,
    string? ProfileId,
    string? ProfileRevision);
