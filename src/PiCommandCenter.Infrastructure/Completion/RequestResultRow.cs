namespace PiCommandCenter.Infrastructure.Completion;

/// <summary>EF row for SPEC §29 RequestResult.</summary>
public sealed class RequestResultRow
{
    public Guid RequestId { get; init; }

    public string SummaryMarkdown { get; init; } = string.Empty;

    public string ChangedFilesJson { get; init; } = "[]";

    public string ReviewFindingsJson { get; init; } = "[]";

    public string VerificationSummaryJson { get; init; } = "{}";

    public long CreatedAtUtcTicks { get; init; }
}
