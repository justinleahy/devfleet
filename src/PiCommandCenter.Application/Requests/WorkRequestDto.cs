namespace PiCommandCenter.Application.Requests;

/// <summary>
/// Read model of a work request.
/// </summary>
public sealed record WorkRequestDto(
    Guid Id,
    Guid ProjectId,
    int Kind,
    string KindName,
    int Priority,
    string PriorityName,
    int RiskLevel,
    string RiskLevelName,
    int Status,
    string StatusName,
    int? BlockedPhase,
    string? BlockedPhaseName,
    string Title,
    string Prompt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    SchedulingStatusDto? SchedulingStatus = null,
    ExecutionAssignmentProjectionDto? Assignment = null,
    Guid? OriginalRequestId = null);
