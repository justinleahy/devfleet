namespace PiCommandCenter.Node.Verification;

/// <summary>
/// Trusted verification profiles bound from the <c>Verification</c> configuration section.
/// Agent prompts never supply executables or arguments.
/// </summary>
public sealed class VerificationOptions
{
    public const string SectionName = "Verification";

    /// <summary>Named shared resource acquired for every verification run (SPEC §20.1).</summary>
    public const string ProjectBuildResource = "project-build";

    /// <summary>Captured stdout/stderr cap per stream, in bytes.</summary>
    public int MaxOutputBytes { get; set; } = 64 * 1024;

    /// <summary>Trusted profiles keyed by id. Empty means verification is not configured.</summary>
    public Dictionary<string, VerificationProfileOptions> Profiles { get; set; } =
        new(StringComparer.Ordinal);
}

/// <summary>One named verification profile from trusted configuration.</summary>
public sealed class VerificationProfileOptions
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Operator-facing label. Defaults to <see cref="Id"/> when omitted.</summary>
    public string? DisplayLabel { get; set; }

    /// <summary>
    /// Stable profile revision. When omitted the node derives a deterministic hash from safe
    /// command metadata and configuration identity, never from executables or arguments.
    /// </summary>
    public string? Revision { get; set; }

    public List<VerificationCommandOptions> Commands { get; set; } = [];
}

/// <summary>One configured command. Executable and arguments are trusted configuration only.</summary>
public sealed class VerificationCommandOptions
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Operator-facing label. Defaults to <see cref="Id"/> when omitted.</summary>
    public string? DisplayLabel { get; set; }

    public string Executable { get; set; } = string.Empty;

    public string[] Arguments { get; set; } = [];

    /// <summary>Repository-relative working directory. <c>.</c> is the canonical root.</summary>
    public string WorkingDirectory { get; set; } = ".";

    /// <summary>
    /// Display-only working-directory label. Defaults to the repository-relative directory.
    /// Never send the raw configuration path or executable.
    /// </summary>
    public string? WorkingDirectoryLabel { get; set; }

    public int TimeoutSeconds { get; set; } = 900;

    public bool Mandatory { get; set; } = true;
}
