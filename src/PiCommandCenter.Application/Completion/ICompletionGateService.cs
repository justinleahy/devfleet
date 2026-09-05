using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Completion;

/// <summary>Objective completion gate over request, sessions, events, reservations, and verification.</summary>
public interface ICompletionGateService
{
    Task<CompletionGateDecision> EvaluateAsync(
        ProjectId projectId,
        Domain.Requests.WorkRequestId requestId,
        string rootSessionId,
        CompletionEvidence evidence,
        CancellationToken cancellationToken = default);

    Task<RequestResultDto?> GetResultAsync(
        Domain.Requests.WorkRequestId requestId,
        CancellationToken cancellationToken = default);
}
