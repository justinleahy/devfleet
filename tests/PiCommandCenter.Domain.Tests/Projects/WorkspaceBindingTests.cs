using PiCommandCenter.Domain.Projects;

namespace PiCommandCenter.Domain.Tests;

public sealed class WorkspaceBindingTests
{
    private static readonly DateTimeOffset DesignatedAt =
        new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static WorkspaceBinding Designate(
        string repositoryPath = "/tmp/fleet",
        DateTimeOffset? at = null) => WorkspaceBinding.Designate(
            ProjectId.New(),
            NodeId.New(),
            repositoryPath,
            at ?? DesignatedAt);

    [Fact]
    public void Workspace_binding_id_rejects_an_empty_value()
    {
        Assert.Throws<ArgumentException>(() => new WorkspaceBindingId(Guid.Empty));
        Assert.NotEqual(Guid.Empty, WorkspaceBindingId.New().Value);
    }

    [Fact]
    public void Designate_creates_a_pending_first_revision()
    {
        var projectId = ProjectId.New();
        var nodeId = NodeId.New();

        var binding = WorkspaceBinding.Designate(
            projectId,
            nodeId,
            "  /tmp/../fleet/  ",
            DesignatedAt);

        Assert.NotEqual(Guid.Empty, binding.Id.Value);
        Assert.Equal(projectId, binding.ProjectId);
        Assert.Equal(nodeId, binding.NodeId);
        Assert.Equal("/tmp/../fleet/", binding.RepositoryPath);
        Assert.Null(binding.CanonicalRepositoryPath);
        Assert.Equal(WorkspaceBindingStatus.PendingValidation, binding.Status);
        Assert.Equal(1, binding.ValidationRevision);
        Assert.Null(binding.ValidationCode);
        Assert.Null(binding.ValidationDetail);
        Assert.Null(binding.ValidatedAt);
        Assert.Equal(DesignatedAt, binding.CreatedAt);
        Assert.Equal(DesignatedAt, binding.UpdatedAt);
        Assert.Equal(1, binding.Version);
    }

    [Fact]
    public void Redesignate_advances_the_revision_and_resets_validation()
    {
        var binding = Designate();
        var originalId = binding.Id;
        var originalProjectId = binding.ProjectId;
        var validatedAt = DesignatedAt.AddMinutes(1);
        var newNodeId = NodeId.New();
        var redesignedAt = DesignatedAt.AddMinutes(2);
        Assert.True(binding.ApplyValidationResult(
            binding.NodeId,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Workspace validation succeeded.",
            "/srv/fleet",
            validatedAt));

        binding.Redesignate(newNodeId, "/srv/other", redesignedAt);

        Assert.Equal(originalId, binding.Id);
        Assert.Equal(originalProjectId, binding.ProjectId);
        Assert.Equal(newNodeId, binding.NodeId);
        Assert.Equal("/srv/other", binding.RepositoryPath);
        Assert.Equal(2, binding.ValidationRevision);
        Assert.Equal(WorkspaceBindingStatus.PendingValidation, binding.Status);
        Assert.Null(binding.CanonicalRepositoryPath);
        Assert.Null(binding.ValidationCode);
        Assert.Null(binding.ValidationDetail);
        Assert.Null(binding.ValidatedAt);
        Assert.Equal(redesignedAt, binding.UpdatedAt);
        Assert.Equal(3, binding.Version);
    }

    [Fact]
    public void Apply_valid_result_stores_the_node_supplied_canonical_path()
    {
        var binding = Designate("/tmp/fleet-link");
        var validatedAt = DesignatedAt.AddMinutes(1);

        var applied = binding.ApplyValidationResult(
            binding.NodeId,
            revision: 1,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Workspace validation succeeded.",
            "/srv/repos/fleet",
            validatedAt);

        Assert.True(applied);
        Assert.Equal("/tmp/fleet-link", binding.RepositoryPath);
        Assert.Equal("/srv/repos/fleet", binding.CanonicalRepositoryPath);
        Assert.Equal(WorkspaceBindingStatus.Valid, binding.Status);
        Assert.Equal(WorkspaceBinding.ValidValidationCode, binding.ValidationCode);
        Assert.Equal("Workspace validation succeeded.", binding.ValidationDetail);
        Assert.Equal(validatedAt, binding.ValidatedAt);
        Assert.Equal(validatedAt, binding.UpdatedAt);
        Assert.Equal(2, binding.Version);
    }

    [Fact]
    public void Apply_invalid_result_stores_bounded_safe_code_and_detail()
    {
        var binding = Designate();
        var detail = new string('x', WorkspaceBinding.MaxValidationDetailLength + 100);
        var validatedAt = DesignatedAt.AddMinutes(1);

        var applied = binding.ApplyValidationResult(
            binding.NodeId,
            revision: 1,
            WorkspaceBindingStatus.Invalid,
            "path_missing",
            detail,
            canonicalRepositoryPath: null,
            validatedAt);

        Assert.True(applied);
        Assert.Equal(WorkspaceBindingStatus.Invalid, binding.Status);
        Assert.Equal("path_missing", binding.ValidationCode);
        Assert.Equal(WorkspaceBinding.MaxValidationDetailLength, binding.ValidationDetail!.Length);
        Assert.Null(binding.CanonicalRepositoryPath);
        Assert.Equal(validatedAt, binding.ValidatedAt);
    }

    [Fact]
    public void Apply_ignores_results_for_another_node_or_revision()
    {
        var binding = Designate();
        var originalNodeId = binding.NodeId;
        binding.Redesignate(NodeId.New(), "/tmp/redesignated", DesignatedAt.AddMinutes(1));
        var version = binding.Version;

        Assert.False(binding.ApplyValidationResult(
            originalNodeId,
            revision: 1,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            validationDetail: null,
            "/tmp/stale",
            DesignatedAt.AddMinutes(2)));
        Assert.False(binding.ApplyValidationResult(
            NodeId.New(),
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            validationDetail: null,
            "/tmp/wrong-node",
            DesignatedAt.AddMinutes(2)));

        Assert.Equal(version, binding.Version);
        Assert.Equal(WorkspaceBindingStatus.PendingValidation, binding.Status);
        Assert.Null(binding.CanonicalRepositoryPath);
        Assert.Null(binding.ValidatedAt);
    }

    [Fact]
    public void Validation_results_enforce_bounded_safe_invariants()
    {
        var binding = Designate();
        var revision = binding.ValidationRevision;
        var at = DesignatedAt.AddMinutes(1);

        Assert.Throws<ArgumentException>(() => binding.ApplyValidationResult(
            binding.NodeId,
            revision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            null,
            canonicalRepositoryPath: null,
            at));
        Assert.Throws<ArgumentException>(() => binding.ApplyValidationResult(
            binding.NodeId,
            revision,
            WorkspaceBindingStatus.Valid,
            "not_valid",
            null,
            "/tmp/fleet",
            at));
        Assert.Throws<ArgumentException>(() => binding.ApplyValidationResult(
            binding.NodeId,
            revision,
            WorkspaceBindingStatus.Invalid,
            new string('a', WorkspaceBinding.MaxValidationCodeLength + 1),
            "Missing.",
            null,
            at));
        Assert.Throws<ArgumentException>(() => binding.ApplyValidationResult(
            binding.NodeId,
            revision,
            WorkspaceBindingStatus.Invalid,
            "path-missing",
            "Missing.",
            null,
            at));
        Assert.Throws<ArgumentException>(() => binding.ApplyValidationResult(
            binding.NodeId,
            revision,
            WorkspaceBindingStatus.Invalid,
            "path_missing",
            "unsafe\ndetail",
            null,
            at));
        Assert.Throws<ArgumentException>(() => binding.ApplyValidationResult(
            binding.NodeId,
            revision,
            WorkspaceBindingStatus.PendingValidation,
            "pending",
            null,
            null,
            at));

        Assert.Equal(1, binding.Version);
        Assert.Equal(WorkspaceBindingStatus.PendingValidation, binding.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("/tmp/unsafe\npath")]
    public void Designate_rejects_non_absolute_or_unsafe_paths(string repositoryPath)
    {
        Assert.Throws<ArgumentException>(() => Designate(repositoryPath));
    }

    [Fact]
    public void Rehydrate_restores_a_valid_persisted_binding()
    {
        var id = WorkspaceBindingId.New();
        var projectId = ProjectId.New();
        var nodeId = NodeId.New();
        var createdAt = DesignatedAt.AddDays(-2);
        var validatedAt = DesignatedAt.AddDays(-1);

        var binding = WorkspaceBinding.Rehydrate(
            id,
            projectId,
            nodeId,
            " /tmp/fleet-link ",
            "/srv/repos/fleet",
            WorkspaceBindingStatus.Valid,
            validationRevision: 7,
            WorkspaceBinding.ValidValidationCode,
            "Workspace validation succeeded.",
            validatedAt,
            createdAt,
            updatedAt: validatedAt,
            version: 11);

        Assert.Equal(id, binding.Id);
        Assert.Equal(projectId, binding.ProjectId);
        Assert.Equal(nodeId, binding.NodeId);
        Assert.Equal("/tmp/fleet-link", binding.RepositoryPath);
        Assert.Equal("/srv/repos/fleet", binding.CanonicalRepositoryPath);
        Assert.Equal(WorkspaceBindingStatus.Valid, binding.Status);
        Assert.Equal(7, binding.ValidationRevision);
        Assert.Equal(WorkspaceBinding.ValidValidationCode, binding.ValidationCode);
        Assert.Equal("Workspace validation succeeded.", binding.ValidationDetail);
        Assert.Equal(validatedAt, binding.ValidatedAt);
        Assert.Equal(createdAt, binding.CreatedAt);
        Assert.Equal(validatedAt, binding.UpdatedAt);
        Assert.Equal(11, binding.Version);
    }

    [Fact]
    public void Rehydrate_rejects_non_positive_or_inconsistent_persisted_state()
    {
        var id = WorkspaceBindingId.New();
        var projectId = ProjectId.New();
        var nodeId = NodeId.New();

        Assert.Throws<ArgumentException>(() => WorkspaceBinding.Rehydrate(
            id, projectId, nodeId, "/tmp/fleet", null,
            WorkspaceBindingStatus.PendingValidation, validationRevision: 0,
            null, null, null, DesignatedAt, DesignatedAt, version: 1));
        Assert.Throws<ArgumentException>(() => WorkspaceBinding.Rehydrate(
            id, projectId, nodeId, "/tmp/fleet", null,
            WorkspaceBindingStatus.PendingValidation, validationRevision: 1,
            null, null, DesignatedAt, DesignatedAt, DesignatedAt, version: 1));
        Assert.Throws<ArgumentException>(() => WorkspaceBinding.Rehydrate(
            id, projectId, nodeId, "/tmp/fleet", "/tmp/fleet",
            WorkspaceBindingStatus.Invalid, validationRevision: 1,
            "path_missing", "Missing.", DesignatedAt,
            DesignatedAt, DesignatedAt, version: 1));
        Assert.Throws<ArgumentException>(() => WorkspaceBinding.Rehydrate(
            id, projectId, nodeId, "/tmp/fleet", null,
            WorkspaceBindingStatus.PendingValidation, validationRevision: 1,
            null, null, null, DesignatedAt, DesignatedAt, version: 0));
    }
}
