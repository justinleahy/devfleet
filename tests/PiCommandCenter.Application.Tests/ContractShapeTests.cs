using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Tests;

public class ProjectValidationReportTests
{
    [Fact]
    public void Success_report_is_valid_without_errors()
    {
        Assert.True(ProjectValidationReport.Success.IsValid);
        Assert.Empty(ProjectValidationReport.Success.Errors);
    }

    [Fact]
    public void Failure_report_carries_every_error()
    {
        var report = ProjectValidationReport.Failure(["Repository path is outside an approved root.", "Default branch is empty."]);

        Assert.False(report.IsValid);
        Assert.Equal(2, report.Errors.Count);
    }
}

public class ProjectRegistrationExceptionTests
{
    [Fact]
    public void Validation_exception_exposes_the_errors_that_map_to_http_400()
    {
        var exception = new ProjectValidationException(["Repository path must exist."]);

        Assert.Single(exception.Errors);
        Assert.Equal("Repository path must exist.", exception.Errors[0]);
    }

    [Fact]
    public void Duplicate_exception_exposes_the_colliding_path_that_maps_to_http_409()
    {
        var exception = new DuplicateProjectException("/tmp/fleet");

        Assert.Equal("/tmp/fleet", exception.RepositoryPath);
        Assert.Contains("/tmp/fleet", exception.Message);
    }

    [Fact]
    public void Not_found_exception_exposes_the_missing_id_that_maps_to_http_404()
    {
        var id = Guid.NewGuid();
        var exception = new ProjectNotFoundException(id);

        Assert.Equal(id, exception.ProjectId);
    }
}

public class WorkRequestDtoShapeTests
{
    [Fact]
    public void Dto_reports_numeric_and_named_enum_values_together()
    {
        var projectId = Guid.NewGuid();
        var dto = new WorkRequestDto(
            Guid.NewGuid(),
            projectId,
            (int)WorkRequestKind.Development,
            WorkRequestKind.Development.ToString(),
            (int)RequestPriority.Urgent,
            RequestPriority.Urgent.ToString(),
            (int)RiskLevel.Standard,
            RiskLevel.Standard.ToString(),
            (int)WorkRequestStatus.Queued,
            WorkRequestStatus.Queued.ToString(),
            BlockedPhase: null,
            BlockedPhaseName: null,
            "Fix the queue",
            "Fix the ordering",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            Version: 1);

        Assert.Equal((int)WorkRequestKind.Development, dto.Kind);
        Assert.Equal("Development", dto.KindName);
        Assert.Equal((int)RequestPriority.Urgent, dto.Priority);
        Assert.Equal("Urgent", dto.PriorityName);
        Assert.Equal((int)RiskLevel.Standard, dto.RiskLevel);
        Assert.Equal("Standard", dto.RiskLevelName);
        Assert.Equal((int)WorkRequestStatus.Queued, dto.Status);
        Assert.Equal("Queued", dto.StatusName);
        Assert.Null(dto.BlockedPhase);
        Assert.Null(dto.BlockedPhaseName);
        Assert.Equal(projectId, dto.ProjectId);
        Assert.Equal(1, dto.Version);
    }

    [Fact]
    public void Queue_command_carries_classification_title_and_prompt()
    {
        var command = new QueueWorkRequestCommand(
            WorkRequestKind.Review,
            RequestPriority.High,
            RiskLevel.Small,
            "Review",
            "Review the diff");

        Assert.Equal(WorkRequestKind.Review, command.Kind);
        Assert.Equal(RequestPriority.High, command.Priority);
        Assert.Equal(RiskLevel.Small, command.RiskLevel);
        Assert.Equal("Review", command.Title);
        Assert.Equal("Review the diff", command.Prompt);
    }

    [Fact]
    public void Register_command_carries_every_registration_field()
    {
        var command = new RegisterProjectCommand(
            "Fleet",
            "/tmp/fleet",
            "main",
            Enabled: true,
            MaxActiveWriteRequests: 2,
            MaxReadOnlyRequests: 4,
            MaxChildAgentsPerRequest: 1,
            RequireCleanStart: true,
            CreateRequestBranch: true,
            CreateRequestCommit: false,
            AutoMerge: false);

        Assert.Equal("Fleet", command.DisplayName);
        Assert.Equal("/tmp/fleet", command.RepositoryPath);
        Assert.Equal("main", command.DefaultBranch);
        Assert.True(command.Enabled);
        Assert.Equal(2, command.MaxActiveWriteRequests);
        Assert.Equal(4, command.MaxReadOnlyRequests);
        Assert.Equal(1, command.MaxChildAgentsPerRequest);
        Assert.True(command.RequireCleanStart);
        Assert.True(command.CreateRequestBranch);
        Assert.False(command.CreateRequestCommit);
        Assert.False(command.AutoMerge);
    }
}
