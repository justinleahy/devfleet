using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.VerificationPolicy;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;

namespace PiCommandCenter.Application.Tests.VerificationPolicy;

public sealed class ProjectVerificationPolicyServiceTests
{
    [Fact]
    public async Task Select_persists_the_fence_captured_before_node_validation()
    {
        var world = World.Bound();
        var gateway = new RecordingGateway(Accept(world.Binding, "dotnet-ci", "rev-3"));
        var service = new ProjectVerificationPolicyService(world.Catalog, gateway);

        var selected = await service.SelectAsync(world.ProjectId, "dotnet-ci", "rev-3");

        Assert.Equal("dotnet-ci", selected.TrustedVerificationProfileId);
        Assert.Equal("rev-3", selected.TrustedVerificationProfileRevision);
        Assert.Equal(world.Binding.NodeId, gateway.ValidatedNodeId);
        Assert.Equal(world.Binding.Id, gateway.ValidatedRequest?.WorkspaceBindingId);
        Assert.Equal(world.Binding.ValidationRevision, gateway.ValidatedRequest?.WorkspaceBindingRevision);
        Assert.Equal(world.Binding.Id, world.Catalog.PersistedBindingId?.Value);
        Assert.Equal(world.Binding.NodeId, world.Catalog.PersistedNodeId?.Value);
        Assert.Equal(world.Binding.ValidationRevision, world.Catalog.PersistedRevision);
        Assert.Equal(1, world.Catalog.PersistedProjectVersion);
    }

    [Fact]
    public async Task Clear_persists_using_the_same_captured_fence()
    {
        var world = World.Bound(trustedProfileId: "dotnet-ci", trustedRevision: "rev-3");
        var gateway = new RecordingGateway(Accept(world.Binding, profileId: null, profileRevision: null));
        var service = new ProjectVerificationPolicyService(world.Catalog, gateway);

        var cleared = await service.SelectAsync(world.ProjectId, profileId: null, profileRevision: null);

        Assert.Null(cleared.TrustedVerificationProfileId);
        Assert.Null(cleared.TrustedVerificationProfileRevision);
        Assert.Equal(world.Binding.Id, world.Catalog.PersistedBindingId?.Value);
        Assert.Equal(world.Binding.ValidationRevision, world.Catalog.PersistedRevision);
    }

    [Fact]
    public async Task Select_fails_and_stays_unchanged_when_binding_id_changes_after_validation()
    {
        await AssertRaceRejectsAndLeavesUnchangedAsync(
            mutate: world => world.Catalog.CurrentBinding = world.Binding with { Id = Guid.NewGuid() });
    }

    [Fact]
    public async Task Select_fails_and_stays_unchanged_when_binding_node_changes_after_validation()
    {
        await AssertRaceRejectsAndLeavesUnchangedAsync(
            mutate: world => world.Catalog.CurrentBinding = world.Binding with { NodeId = Guid.NewGuid() });
    }

    [Fact]
    public async Task Select_fails_and_stays_unchanged_when_validation_revision_changes_after_validation()
    {
        await AssertRaceRejectsAndLeavesUnchangedAsync(
            mutate: world => world.Catalog.CurrentBinding = world.Binding with
            {
                ValidationRevision = world.Binding.ValidationRevision + 1,
            });
    }

    [Fact]
    public async Task Select_fails_and_stays_unchanged_when_project_version_changes_after_validation()
    {
        await AssertRaceRejectsAndLeavesUnchangedAsync(
            mutate: world => world.Catalog.CurrentProjectVersion = world.Catalog.Project.Version + 1);
    }

    [Fact]
    public async Task Clear_fails_and_stays_unchanged_when_the_binding_fence_changes_after_validation()
    {
        var world = World.Bound(trustedProfileId: "dotnet-ci", trustedRevision: "rev-3");
        var gateway = new RecordingGateway(
            Accept(world.Binding, profileId: null, profileRevision: null),
            onValidate: () => world.Catalog.CurrentBinding = world.Binding with
            {
                ValidationRevision = world.Binding.ValidationRevision + 1,
            });
        var service = new ProjectVerificationPolicyService(world.Catalog, gateway);

        await Assert.ThrowsAsync<VerificationPolicySelectionException>(
            () => service.SelectAsync(world.ProjectId, profileId: null, profileRevision: null));

        Assert.Equal("dotnet-ci", world.Catalog.Project.TrustedVerificationProfileId);
        Assert.Equal("rev-3", world.Catalog.Project.TrustedVerificationProfileRevision);
        Assert.False(world.Catalog.Persisted);
    }

    [Fact]
    public async Task Select_does_not_persist_when_the_node_rejects_validation()
    {
        var world = World.Bound();
        var gateway = new RecordingGateway(new VerificationProfileSelectionResultMessage(
            Accepted: false,
            VerificationPolicySelectionCodes.Missing,
            Detail: "unknown profile",
            ProfileId: null,
            ProfileRevision: null));
        var service = new ProjectVerificationPolicyService(world.Catalog, gateway);

        var error = await Assert.ThrowsAsync<VerificationPolicySelectionException>(
            () => service.SelectAsync(world.ProjectId, "dotnet-ci", "rev-3"));

        Assert.Equal("unknown profile", error.Message);
        Assert.False(world.Catalog.Persisted);
        Assert.Null(world.Catalog.Project.TrustedVerificationProfileId);
    }

    private static async Task AssertRaceRejectsAndLeavesUnchangedAsync(Action<World> mutate)
    {
        var world = World.Bound();
        var gateway = new RecordingGateway(
            Accept(world.Binding, "dotnet-ci", "rev-3"),
            onValidate: () => mutate(world));
        var service = new ProjectVerificationPolicyService(world.Catalog, gateway);

        await Assert.ThrowsAsync<VerificationPolicySelectionException>(
            () => service.SelectAsync(world.ProjectId, "dotnet-ci", "rev-3"));

        Assert.Null(world.Catalog.Project.TrustedVerificationProfileId);
        Assert.Null(world.Catalog.Project.TrustedVerificationProfileRevision);
        Assert.False(world.Catalog.Persisted);
    }

    private static VerificationProfileSelectionResultMessage Accept(
        WorkspaceBindingDto binding,
        string? profileId,
        string? profileRevision) =>
        new(
            Accepted: true,
            profileId is null ? VerificationPolicySelectionCodes.Cleared : VerificationPolicySelectionCodes.Accepted,
            Detail: string.Empty,
            profileId,
            profileRevision);

    private sealed class World
    {
        private World(FencedProjectCatalog catalog, WorkspaceBindingDto binding)
        {
            Catalog = catalog;
            Binding = binding;
        }

        public FencedProjectCatalog Catalog { get; }

        public WorkspaceBindingDto Binding { get; }

        public ProjectId ProjectId => new(Catalog.Project.Id);

        public static World Bound(string? trustedProfileId = null, string? trustedRevision = null)
        {
            var now = DateTimeOffset.UtcNow;
            var projectId = Guid.NewGuid();
            var binding = new WorkspaceBindingDto(
                Guid.NewGuid(),
                projectId,
                Guid.NewGuid(),
                RepositoryPath: "/node/workspaces/fleet",
                CanonicalRepositoryPath: "/canonical/fleet",
                WorkspaceBindingStatus.Valid,
                ValidationRevision: 4,
                ValidationCode: WorkspaceBinding.ValidValidationCode,
                ValidationDetail: null,
                ValidatedAt: now,
                now,
                now,
                Version: 2);
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
                trustedProfileId,
                trustedRevision,
                now,
                now,
                Version: 1,
                binding);
            return new World(new FencedProjectCatalog(project, binding), binding);
        }
    }

    private sealed class FencedProjectCatalog : IProjectCatalog
    {
        public FencedProjectCatalog(ProjectDto project, WorkspaceBindingDto binding)
        {
            Project = project;
            CurrentBinding = binding;
            CurrentProjectVersion = project.Version;
        }

        public ProjectDto Project { get; private set; }

        public WorkspaceBindingDto? CurrentBinding { get; set; }

        public bool Persisted { get; private set; }

        public WorkspaceBindingId? PersistedBindingId { get; private set; }

        public NodeId? PersistedNodeId { get; private set; }

        public long? PersistedRevision { get; private set; }

        public long? PersistedProjectVersion { get; private set; }

        public long CurrentProjectVersion { get; set; }

        public Task<IReadOnlyList<ProjectDto>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectDto> GetAsync(ProjectId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Project);

        public Task<ProjectValidationReport> ValidateAsync(
            RegisterProjectCommand command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectDto> RegisterAsync(
            RegisterProjectCommand command,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectDto> SelectTrustedVerificationProfileAsync(
            ProjectId id,
            WorkspaceBindingId workspaceBindingId,
            NodeId nodeId,
            long validationRevision,
            long expectedProjectVersion,
            string? profileId,
            string? profileRevision,
            CancellationToken cancellationToken = default)
        {
            var current = CurrentBinding;
            if (current is null
                || current.Id != workspaceBindingId.Value
                || current.NodeId != nodeId.Value
                || current.ValidationRevision != validationRevision
                || CurrentProjectVersion != expectedProjectVersion)
            {
                throw new VerificationPolicySelectionException(
                    "The designated workspace changed before the verification policy selection could be persisted.");
            }

            Persisted = true;
            PersistedBindingId = workspaceBindingId;
            PersistedNodeId = nodeId;
            PersistedRevision = validationRevision;
            PersistedProjectVersion = expectedProjectVersion;
            Project = Project with
            {
                TrustedVerificationProfileId = profileId,
                TrustedVerificationProfileRevision = profileRevision,
                Binding = current,
                Version = CurrentProjectVersion + 1,
            };
            CurrentProjectVersion = Project.Version;
            return Task.FromResult(Project);
        }
    }

    private sealed class RecordingGateway(
        VerificationProfileSelectionResultMessage result,
        Action? onValidate = null) : INodeVerificationPolicyGateway
    {
        public Guid? ValidatedNodeId { get; private set; }

        public VerificationProfileSelectionRequestMessage? ValidatedRequest { get; private set; }

        public Task<VerificationPolicyCatalogMessage> GetCatalogAsync(
            Guid nodeId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VerificationProfileSelectionResultMessage> ValidateSelectionAsync(
            Guid nodeId,
            VerificationProfileSelectionRequestMessage request,
            CancellationToken cancellationToken = default)
        {
            ValidatedNodeId = nodeId;
            ValidatedRequest = request;
            onValidate?.Invoke();
            return Task.FromResult(result);
        }
    }
}
