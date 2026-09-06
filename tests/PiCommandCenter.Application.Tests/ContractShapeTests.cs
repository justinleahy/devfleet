using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain.Projects;
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
        var report = ProjectValidationReport.Failure(["Display name is required.", "Default branch is empty."]);

        Assert.False(report.IsValid);
        Assert.Equal(2, report.Errors.Count);
    }
}

public class ProjectRegistrationExceptionTests
{
    [Fact]
    public void Validation_exception_exposes_the_errors_that_map_to_http_400()
    {
        var exception = new ProjectValidationException(["Display name is required."]);

        Assert.Single(exception.Errors);
        Assert.Equal("Display name is required.", exception.Errors[0]);
    }

    [Fact]
    public void Not_found_exception_exposes_the_missing_id_that_maps_to_http_404()
    {
        var id = Guid.NewGuid();
        var exception = new ProjectNotFoundException(id);

        Assert.Equal(id, exception.ProjectId);
    }
}

public class ProjectDtoShapeTests
{
    [Fact]
    public void Registration_command_carries_only_project_metadata()
    {
        var command = new RegisterProjectCommand(
            "Fleet",
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
        Assert.Equal("main", command.DefaultBranch);
        Assert.True(command.Enabled);
        Assert.Equal(2, command.MaxActiveWriteRequests);
        Assert.Equal(4, command.MaxReadOnlyRequests);
        Assert.Equal(1, command.MaxChildAgentsPerRequest);
        Assert.True(command.RequireCleanStart);
        Assert.True(command.CreateRequestBranch);
        Assert.False(command.CreateRequestCommit);
        Assert.False(command.AutoMerge);
        Assert.Null(typeof(RegisterProjectCommand).GetProperty("RepositoryPath"));
        Assert.Null(typeof(RegisterProjectCommand).GetProperty("NodeId"));
    }

    [Fact]
    public void ProjectDto_exposes_an_optional_typed_workspace_binding()
    {
        var projectId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var project = new ProjectDto(
            projectId,
            "Fleet",
            "main",
            Enabled: true,
            MaxActiveWriteRequests: 2,
            MaxReadOnlyRequests: 4,
            MaxChildAgentsPerRequest: 1,
            RequireCleanStart: true,
            CreateRequestBranch: true,
            CreateRequestCommit: false,
            AutoMerge: false,
            TrustedVerificationProfileId: null,
            TrustedVerificationProfileRevision: null,
            now,
            now,
            Version: 1,
            Binding: null);

        Assert.Null(project.Binding);
        Assert.Null(project.TrustedVerificationProfileId);
        Assert.Null(project.TrustedVerificationProfileRevision);

        var binding = new WorkspaceBindingDto(
            Guid.NewGuid(),
            projectId,
            Guid.NewGuid(),
            RepositoryPath: "/requested/fleet",
            CanonicalRepositoryPath: null,
            WorkspaceBindingStatus.PendingValidation,
            ValidationRevision: 3,
            ValidationCode: null,
            ValidationDetail: null,
            ValidatedAt: null,
            now,
            now,
            Version: 1);

        var boundProject = project with { Binding = binding };

        var projectedBinding = Assert.IsType<WorkspaceBindingDto>(boundProject.Binding);
        Assert.Equal("/requested/fleet", projectedBinding.RepositoryPath);
        Assert.Null(projectedBinding.CanonicalRepositoryPath);
        Assert.Equal(WorkspaceBindingStatus.PendingValidation, projectedBinding.Status);
        Assert.Equal(3, projectedBinding.ValidationRevision);
        Assert.Null(typeof(ProjectDto).GetProperty("NodeId"));
        Assert.Null(typeof(ProjectDto).GetProperty("RepositoryPath"));
        Assert.Null(typeof(ProjectDto).GetProperty("CanonicalRepositoryPath"));
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
        Assert.Null(dto.SchedulingStatus);
        Assert.Null(dto.Assignment);
    }

    [Fact]
    public void Dto_exposes_nullable_scheduling_and_assignment_projections()
    {
        var schedulingStatusProperty = typeof(WorkRequestDto)
            .GetProperty(nameof(WorkRequestDto.SchedulingStatus))!;
        var assignmentProperty = typeof(WorkRequestDto)
            .GetProperty(nameof(WorkRequestDto.Assignment))!;
        var nullability = new System.Reflection.NullabilityInfoContext();

        Assert.Equal(typeof(SchedulingStatusDto), schedulingStatusProperty.PropertyType);
        Assert.Equal(
            System.Reflection.NullabilityState.Nullable,
            nullability.Create(schedulingStatusProperty).ReadState);
        Assert.Equal(typeof(ExecutionAssignmentProjectionDto), assignmentProperty.PropertyType);
        Assert.Equal(
            System.Reflection.NullabilityState.Nullable,
            nullability.Create(assignmentProperty).ReadState);
    }

    [Fact]
    public void Assignment_projection_preserves_history_without_claim_token()
    {
        var requestId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var workspaceBindingId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var assignedAt = DateTimeOffset.UtcNow;
        var leaseExpiresAt = assignedAt.AddMinutes(5);
        var lastRenewedAt = assignedAt.AddMinutes(1);
        var lastReconciledAt = assignedAt.AddMinutes(2);
        var terminalAt = assignedAt.AddMinutes(3);
        var assignment = new ExecutionAssignmentProjectionDto(
            requestId,
            projectId,
            workspaceBindingId,
            nodeId,
            "/repos/devfleet",
            "main",
            BindingValidationRevisionSnapshot: 7,
            ExecutionAssignmentState.Completed,
            assignedAt,
            leaseExpiresAt,
            lastRenewedAt,
            lastReconciledAt,
            terminalAt,
            VerificationPolicyRevision: "policy-rev",
            BaselineVersion: "baseline-1",
            TrustedVerificationProfileId: "ci",
            TrustedVerificationProfileRevision: "profile-rev",
            MandatoryCommandIdsJson: "[\"repository-integrity\"]");

        Assert.Equal(requestId, assignment.RequestId);
        Assert.Equal(projectId, assignment.ProjectId);
        Assert.Equal(workspaceBindingId, assignment.WorkspaceBindingId);
        Assert.Equal(nodeId, assignment.NodeIdSnapshot);
        Assert.Equal("/repos/devfleet", assignment.CanonicalRepositoryPathSnapshot);
        Assert.Equal("main", assignment.DefaultBranchSnapshot);
        Assert.Equal(7, assignment.BindingValidationRevisionSnapshot);
        Assert.Equal(ExecutionAssignmentState.Completed, assignment.State);
        Assert.Equal(assignedAt, assignment.AssignedAt);
        Assert.Equal(leaseExpiresAt, assignment.LeaseExpiresAt);
        Assert.Equal(lastRenewedAt, assignment.LastRenewedAt);
        Assert.Equal(lastReconciledAt, assignment.LastReconciledAt);
        Assert.Equal(terminalAt, assignment.TerminalAt);
        Assert.Equal("policy-rev", assignment.VerificationPolicyRevision);
        Assert.Equal("baseline-1", assignment.BaselineVersion);
        Assert.Equal("ci", assignment.TrustedVerificationProfileId);
        Assert.Equal("profile-rev", assignment.TrustedVerificationProfileRevision);
        Assert.Equal("[\"repository-integrity\"]", assignment.MandatoryCommandIdsJson);
        Assert.Null(typeof(ExecutionAssignmentProjectionDto).GetProperty("ClaimToken"));
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
}
