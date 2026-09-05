namespace PiCommandCenter.Application.Verification;

/// <summary>Named set of trusted verification commands (SPEC §20).</summary>
public sealed record VerificationProfileDefinition(
    string Id,
    IReadOnlyList<VerificationCommandDefinition> Commands);
