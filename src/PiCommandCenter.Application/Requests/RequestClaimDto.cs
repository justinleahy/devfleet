using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Requests;

/// <summary>
/// Read model of a node's claim on a work request.
/// </summary>
public sealed record RequestClaimDto(
    Guid RequestId,
    Guid ProjectId,
    Guid NodeId,
    string ClaimToken,
    DateTimeOffset ClaimedAt,
    DateTimeOffset LeaseExpiresAt,
    string RepositoryPath,
    string DefaultBranch,
    string Title,
    string Prompt,
    string Kind,
    string RiskLevel,
    bool CreateRequestBranch,
    bool CreateRequestCommit);
