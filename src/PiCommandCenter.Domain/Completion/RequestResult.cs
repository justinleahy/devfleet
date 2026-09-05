using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Domain.Completion;

/// <summary>Persisted request-level completion result (SPEC §29 RequestResult).</summary>
public sealed class RequestResult
{
    private RequestResult(
        WorkRequestId requestId,
        string summaryMarkdown,
        string changedFilesJson,
        string reviewFindingsJson,
        string verificationSummaryJson,
        DateTimeOffset createdAt)
    {
        RequestId = requestId;
        SummaryMarkdown = summaryMarkdown;
        ChangedFilesJson = changedFilesJson;
        ReviewFindingsJson = reviewFindingsJson;
        VerificationSummaryJson = verificationSummaryJson;
        CreatedAt = createdAt;
    }

    public WorkRequestId RequestId { get; }

    public string SummaryMarkdown { get; }

    public string ChangedFilesJson { get; }

    public string ReviewFindingsJson { get; }

    public string VerificationSummaryJson { get; }

    public DateTimeOffset CreatedAt { get; }

    public static RequestResult Create(
        WorkRequestId requestId,
        string summaryMarkdown,
        string changedFilesJson,
        string reviewFindingsJson,
        string verificationSummaryJson,
        DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(summaryMarkdown))
        {
            throw new ArgumentException("Result summary must not be blank.", nameof(summaryMarkdown));
        }

        return new RequestResult(
            requestId,
            summaryMarkdown.Trim(),
            string.IsNullOrWhiteSpace(changedFilesJson) ? "[]" : changedFilesJson,
            string.IsNullOrWhiteSpace(reviewFindingsJson) ? "[]" : reviewFindingsJson,
            string.IsNullOrWhiteSpace(verificationSummaryJson) ? "{}" : verificationSummaryJson,
            createdAt);
    }
}
