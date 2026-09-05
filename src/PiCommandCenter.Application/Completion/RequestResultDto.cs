namespace PiCommandCenter.Application.Completion;

/// <summary>Persisted request result (SPEC §29 RequestResult).</summary>
public sealed record RequestResultDto(
    Guid RequestId,
    string SummaryMarkdown,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<ReviewFinding> ReviewFindings,
    string VerificationSummary,
    DateTimeOffset CreatedAt);
