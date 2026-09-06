using System.ComponentModel.DataAnnotations;
using System.Reflection;

using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Tests;

/// <summary>
/// Shape tests for the node application contracts shared by the hub, the node worker, and the UI.
/// </summary>
public class NodeContractShapeTests
{
    [Fact]
    public void Node_status_is_an_explicit_offline_online_pair()
    {
        Assert.Equal(0, (int)NodeStatus.Offline);
        Assert.Equal(1, (int)NodeStatus.Online);
    }

    [Fact]
    public void NodeDto_carries_the_projection_the_ui_renders()
    {
        var id = Guid.NewGuid();
        var seen = DateTimeOffset.UtcNow;

        var resources = new NodeResourceSnapshotDto(
            seen,
            CpuUsagePercent: 12.5,
            MemoryUsedBytes: 1024L,
            MemoryTotalBytes: 2048L,
            DiskUsedBytes: 4096L,
            DiskTotalBytes: 8192L,
            LoadAverageOneMinute: 0.25,
            UptimeSeconds: 90d);

        var dto = new NodeDto(id, "pi-01", "1.2.3", seen, NodeStatus.Online, "{}", Version: 7, Resources: resources);

        Assert.Equal(id, dto.Id);
        Assert.Equal("pi-01", dto.DisplayName);
        Assert.Equal("1.2.3", dto.AgentVersion);
        Assert.Equal(seen, dto.LastHeartbeatAt);
        Assert.Equal(NodeStatus.Online, dto.Status);
        Assert.Equal("{}", dto.CapabilitiesJson);
        Assert.Equal(7, dto.Version);
        Assert.Equal(resources, dto.Resources);
        Assert.Equal(seen, dto.Resources!.ObservedAt);
        Assert.Equal(12.5, dto.Resources.CpuUsagePercent);
        Assert.Equal(1024L, dto.Resources.MemoryUsedBytes);
        Assert.Equal(2048L, dto.Resources.MemoryTotalBytes);
        Assert.Equal(4096L, dto.Resources.DiskUsedBytes);
        Assert.Equal(8192L, dto.Resources.DiskTotalBytes);
        Assert.Equal(0.25, dto.Resources.LoadAverageOneMinute);
        Assert.Equal(90d, dto.Resources.UptimeSeconds);
    }

    [Fact]
    public void ExecutionAssignmentDto_exposes_the_durable_assignment_and_request_snapshot()
    {
        var requestId = WorkRequestId.New();
        var projectId = ProjectId.New();
        var workspaceBindingId = WorkspaceBindingId.New();
        var nodeId = NodeId.New();
        var assignedAt = DateTimeOffset.UtcNow;
        var leaseExpiresAt = assignedAt.AddMinutes(1);
        var dto = new ExecutionAssignmentDto(
            requestId,
            projectId,
            workspaceBindingId,
            nodeId,
            "/repos/hub",
            "main",
            BindingValidationRevisionSnapshot: 7,
            ExecutionAssignmentState.RecoveryRequired,
            "token",
            assignedAt,
            leaseExpiresAt,
            "Hub request",
            "Do hub work",
            WorkRequestKind.Development,
            RiskLevel.Standard,
            CreateRequestBranch: true,
            CreateRequestCommit: false,
            VerificationPolicyRevision: null,
            BaselineVersion: null,
            TrustedVerificationProfileId: null,
            TrustedVerificationProfileRevision: null,
            MandatoryCommandIdsJson: null);

        Assert.Equal(requestId, dto.RequestId);
        Assert.Equal(projectId, dto.ProjectId);
        Assert.Equal(workspaceBindingId, dto.WorkspaceBindingId);
        Assert.Equal(nodeId, dto.NodeIdSnapshot);
        Assert.Equal("/repos/hub", dto.CanonicalRepositoryPathSnapshot);
        Assert.Equal("main", dto.DefaultBranchSnapshot);
        Assert.Equal(7, dto.BindingValidationRevisionSnapshot);
        Assert.Equal(ExecutionAssignmentState.RecoveryRequired, dto.State);
        Assert.Equal("token", dto.ClaimToken);
        Assert.Equal(assignedAt, dto.AssignedAt);
        Assert.Equal(leaseExpiresAt, dto.LeaseExpiresAt);
        Assert.Equal("Hub request", dto.RequestTitle);
        Assert.Equal("Do hub work", dto.RequestPrompt);
        Assert.Equal(WorkRequestKind.Development, dto.RequestKind);
        Assert.Equal(RiskLevel.Standard, dto.RequestRiskLevel);
        Assert.True(dto.CreateRequestBranch);
        Assert.False(dto.CreateRequestCommit);
        Assert.Null(dto.VerificationPolicyRevision);
        Assert.Null(dto.BaselineVersion);
        Assert.Null(dto.TrustedVerificationProfileId);
        Assert.Null(dto.TrustedVerificationProfileRevision);
        Assert.Null(dto.MandatoryCommandIdsJson);
    }

    [Fact]
    public void ExecutionAssignmentMessage_carries_the_immutable_execution_snapshot()
    {
        var requestId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var workspaceBindingId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var assignedAt = DateTimeOffset.UtcNow;
        var leaseExpiresAt = assignedAt.AddMinutes(1);
        var message = new ExecutionAssignmentMessage(
            requestId,
            projectId,
            workspaceBindingId,
            nodeId,
            "/repos/hub",
            "main",
            BindingValidationRevisionSnapshot: 7,
            State: "RecoveryRequired",
            ClaimToken: "token",
            assignedAt,
            leaseExpiresAt,
            "Hub request",
            "Do hub work",
            RequestKind: "Development",
            RequestRiskLevel: "Standard",
            CreateRequestBranch: true,
            CreateRequestCommit: false);

        Assert.Equal(requestId, message.RequestId);
        Assert.Equal(projectId, message.ProjectId);
        Assert.Equal(workspaceBindingId, message.WorkspaceBindingId);
        Assert.Equal(nodeId, message.NodeIdSnapshot);
        Assert.Equal("/repos/hub", message.CanonicalRepositoryPathSnapshot);
        Assert.Equal("main", message.DefaultBranchSnapshot);
        Assert.Equal(7, message.BindingValidationRevisionSnapshot);
        Assert.Equal("RecoveryRequired", message.State);
        Assert.Equal("token", message.ClaimToken);
        Assert.Equal(assignedAt, message.AssignedAt);
        Assert.Equal(leaseExpiresAt, message.LeaseExpiresAt);
        Assert.Equal("Hub request", message.RequestTitle);
        Assert.Equal("Do hub work", message.RequestPrompt);
        Assert.Equal("Development", message.RequestKind);
        Assert.Equal("Standard", message.RequestRiskLevel);
        Assert.True(message.CreateRequestBranch);
        Assert.False(message.CreateRequestCommit);
    }

    [Fact]
    public void Execution_assignment_service_exposes_claim_and_renew_operations()
    {
        var claimNext = typeof(IExecutionAssignmentService).GetMethod(
            nameof(IExecutionAssignmentService.ClaimNextAsync))!;
        var renew = typeof(IExecutionAssignmentService).GetMethod(
            nameof(IExecutionAssignmentService.RenewAsync))!;

        Assert.Equal(typeof(Task<ExecutionAssignmentDto>), claimNext.ReturnType);
        Assert.Equal(
            new[] { typeof(NodeId), typeof(TimeSpan), typeof(CancellationToken) },
            claimNext.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(typeof(Task<DateTimeOffset>), renew.ReturnType);
        Assert.Equal(
            new[] { typeof(WorkRequestId), typeof(NodeId), typeof(string), typeof(TimeSpan), typeof(CancellationToken) },
            renew.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void Legacy_request_claim_contracts_are_not_exposed()
    {
        var applicationAssembly = typeof(ExecutionAssignmentDto).Assembly;
        var transportAssembly = typeof(ExecutionAssignmentMessage).Assembly;

        Assert.Null(applicationAssembly.GetType("PiCommandCenter.Application.Requests.RequestClaimDto"));
        Assert.Null(applicationAssembly.GetType("PiCommandCenter.Application.Requests.IRequestClaimService"));
        Assert.Null(transportAssembly.GetType("PiCommandCenter.Contracts.NodeTransport.RequestClaimMessage"));
    }

    [Theory]
    [InlineData(typeof(AcquireReservationMessage))]
    [InlineData(typeof(ReservationMutationMessage))]
    [InlineData(typeof(ExpandReservationMessage))]
    [InlineData(typeof(ReleaseReservationMessage))]
    [InlineData(typeof(TransferReservationMessage))]
    [InlineData(typeof(MutationAuthorizationMessage))]
    [InlineData(typeof(MarkRecoveryMessage))]
    [InlineData(typeof(ListReservationsMessage))]
    [InlineData(typeof(SendMailMessage))]
    [InlineData(typeof(ReplyMailMessage))]
    [InlineData(typeof(FetchMailInboxMessage))]
    [InlineData(typeof(FetchMailThreadMessage))]
    [InlineData(typeof(MarkMailReadMessage))]
    [InlineData(typeof(AcknowledgeMailMessage))]
    [InlineData(typeof(AllocateAgentIdentityMessage))]
    [InlineData(typeof(ReleaseAgentIdentityMessage))]
    [InlineData(typeof(FindAgentIdentityMessage))]
    public void Request_scoped_node_operations_require_a_bounded_assignment_fence(Type messageType)
    {
        var projectId = messageType.GetProperty("ProjectId")!;
        var requestId = messageType.GetProperty("RequestId")!;
        var claimToken = messageType.GetProperty("ClaimToken")!;
        var constructorParameters = messageType.GetConstructors().Single().GetParameters();
        var projectIdParameter = constructorParameters.Single(parameter => parameter.Name == "ProjectId");
        var requestIdParameter = constructorParameters.Single(parameter => parameter.Name == "RequestId");
        var claimTokenParameter = constructorParameters.Single(parameter => parameter.Name == "ClaimToken");

        Assert.Equal(typeof(Guid), projectId.PropertyType);
        Assert.Equal(typeof(Guid), requestId.PropertyType);
        Assert.Equal(typeof(string), claimToken.PropertyType);
        Assert.NotNull(claimToken.GetCustomAttribute<RequiredAttribute>());
        Assert.Equal(128, claimToken.GetCustomAttribute<MaxLengthAttribute>()?.Length);

        Assert.False(projectIdParameter.IsOptional);
        Assert.False(projectIdParameter.HasDefaultValue);
        Assert.False(requestIdParameter.IsOptional);
        Assert.False(requestIdParameter.HasDefaultValue);
        Assert.False(claimTokenParameter.IsOptional);
        Assert.False(claimTokenParameter.HasDefaultValue);
    }

    [Theory]
    [InlineData(typeof(NodeEventMessage))]
    [InlineData(typeof(VerificationRunMessage))]
    [InlineData(typeof(BeginTerminalizationMessage))]
    [InlineData(typeof(ConfirmTerminalizationMessage))]
    public void Event_verification_and_completion_commands_require_a_bounded_assignment_fence(Type messageType)
    {
        var claimToken = messageType.GetProperty("ClaimToken")!;
        var constructorParameter = messageType
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(parameter => parameter.Name == "ClaimToken");

        Assert.Equal(typeof(string), claimToken.PropertyType);
        Assert.NotNull(claimToken.GetCustomAttribute<RequiredAttribute>());
        Assert.Equal(128, claimToken.GetCustomAttribute<MaxLengthAttribute>()?.Length);
        Assert.False(constructorParameter.IsOptional);
    }

    [Theory]
    [InlineData(typeof(NodeEventAcknowledgementMessage))]
    [InlineData(typeof(VerificationRunResultMessage))]
    [InlineData(typeof(CompletionGateDecisionMessage))]
    [InlineData(typeof(RequestResultMessage))]
    public void Node_command_responses_do_not_expose_assignment_claim_tokens(Type messageType)
    {
        Assert.Null(messageType.GetProperty("ClaimToken"));
    }

    [Fact]
    public void NodeNotFoundException_identifies_the_missing_node()
    {
        var id = new NodeId(Guid.NewGuid());

        var exception = new NodeNotFoundException(id);

        Assert.Equal(id, exception.Id);
        Assert.Contains(id.ToString(), exception.Message);
    }
}
