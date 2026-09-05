namespace PiCommandCenter.Application.Verification;

/// <summary>Trusted verification command from Node/project configuration (SPEC §20).</summary>
public sealed record VerificationCommandDefinition(
    string Id,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int TimeoutSeconds,
    bool Mandatory);
