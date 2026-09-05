namespace PiCommandCenter.Application.Completion;

/// <summary>Objective evidence submitted to the completion gate.</summary>
public sealed record CompletionEvidence(
    string SummaryMarkdown,
    IReadOnlyList<string>? ChangedFiles,
    IReadOnlyList<ReviewFinding> ReviewFindings,
    string VerificationSummary,
    string? RequestBranch = null,
    string? CheckpointCommitId = null);
