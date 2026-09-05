using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Verification;

/// <summary>Append-only store of verification command runs.</summary>
public interface IVerificationRunStore
{
    /// <summary>Persists a run. Empty <see cref="VerificationRunDto.Id"/> is assigned.</summary>
    Task<VerificationRunDto> RecordAsync(
        VerificationRunDto run,
        CancellationToken cancellationToken = default);

    /// <summary>Lists runs for a request, oldest first.</summary>
    Task<IReadOnlyList<VerificationRunDto>> ListAsync(
        WorkRequestId requestId,
        CancellationToken cancellationToken = default);
}
