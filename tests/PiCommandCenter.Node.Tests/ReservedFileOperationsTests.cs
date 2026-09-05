using PiCommandCenter.Node.Child;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Reservation-aware child filesystem operations (SPEC §18.1): every mutation authorizes the
/// lease + fencing token against the reservation authority <em>before</em> touching the
/// filesystem, a denied decision leaves the repository byte-identical, and the path policy
/// rejects anything outside the canonical workspace. Uses a real temporary repository and a
/// fake gateway — no control plane, no provider network.
/// </summary>
public class ReservedFileOperationsTests : IDisposable
{
    private readonly string _repoRoot = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pi-cc-reserved-ops", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repoRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static MutationLease Lease(long token = 7) => new(Guid.NewGuid(), token);

    private sealed class FakeReservationGateway : INodeReservationGateway
    {
        /// <summary>Maps (targetPath, operation) to a decision; null means authorized.</summary>
        public Func<string, string, MutationAuthorizationResult?>? OnAuthorize { get; set; }

        public List<(string Path, string Operation)> Authorizations { get; } = [];

        public Task<ReservationOperationResult> AcquireAsync(
            Guid projectId, Guid requestId, string ownerSessionId,
            IReadOnlyList<ReservationScopeSpec> scopes, string reason, CancellationToken _) =>
            throw new NotSupportedException();

        public Task<ReservationOperationResult> ExpandAsync(
            Guid leaseId, Guid projectId, long fencingToken, string sessionId,
            IReadOnlyList<ReservationScopeSpec> scopes, CancellationToken _) =>
            throw new NotSupportedException();

        public Task<ReservationOperationResult> ReleaseAsync(
            Guid leaseId, Guid projectId, string sessionId, CancellationToken _) =>
            throw new NotSupportedException();

        public Task<ReservationOperationResult> TransferAsync(
            Guid leaseId, string fromSessionId, string toSessionId, CancellationToken _) =>
            throw new NotSupportedException();

        public Task<ReservationOperationResult> RenewAsync(
            Guid leaseId, long fencingToken, string sessionId, CancellationToken _) =>
            throw new NotSupportedException();

        public Task<MutationAuthorizationResult> AuthorizeAsync(
            Guid leaseId, long fencingToken, string sessionId, string targetPath,
            string operation, CancellationToken _)
        {
            Authorizations.Add((targetPath, operation));
            var decision = OnAuthorize?.Invoke(targetPath, operation)
                ?? new MutationAuthorizationResult(true, null);
            return Task.FromResult(decision);
        }

        public Task<IReadOnlyList<ReservationLeaseInfo>> ListAsync(
            Guid projectId, bool includeReleased, CancellationToken _) =>
            Task.FromResult<IReadOnlyList<ReservationLeaseInfo>>([]);

        public Task<ReservationOperationResult> MarkRecoveryRequiredAsync(
            Guid leaseId, string reason, CancellationToken _) =>
            throw new NotSupportedException();
    }

    private (ReservedFileOperations Ops, FakeReservationGateway Gateway) CreateOps()
    {
        var gateway = new FakeReservationGateway();
        return (new ReservedFileOperations(gateway), gateway);
    }

    private string WriteRepoFile(string relativePath, string content)
    {
        var path = Path.Combine(_repoRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Write_authorizes_before_touching_the_filesystem_and_persists_the_content()
    {
        var (ops, gateway) = CreateOps();

        var result = await ops.WriteTextAsync(_repoRoot, Lease(), "session-a", "src/App/New.cs", "hello");

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal([("src/App/New.cs", "write")], gateway.Authorizations);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_repoRoot, "src/App/New.cs")));
    }

    [Fact]
    public async Task A_denied_mutation_leaves_the_filesystem_untouched()
    {
        var (ops, gateway) = CreateOps();
        var path = WriteRepoFile("src/App/Existing.cs", "original");
        gateway.OnAuthorize = (_, _) => new MutationAuthorizationResult(
            false, new GatewayError("conflict", "lease does not cover the target"));

        var result = await ops.WriteTextAsync(_repoRoot, Lease(), "session-a", "src/App/Existing.cs", "overwritten");

        Assert.False(result.Ok);
        Assert.Equal("conflict", result.ErrorCode);
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_requires_the_search_text_and_applies_an_exact_replacement()
    {
        var (ops, _) = CreateOps();
        WriteRepoFile("docs/README.md", "alpha beta gamma");

        var miss = await ops.EditTextAsync(_repoRoot, Lease(), "session-a", "docs/README.md", "delta", "x");
        Assert.False(miss.Ok);
        Assert.Equal("edit_target_not_found", miss.ErrorCode);
        Assert.Equal("alpha beta gamma", File.ReadAllText(Path.Combine(_repoRoot, "docs/README.md")));

        var hit = await ops.EditTextAsync(_repoRoot, Lease(), "session-a", "docs/README.md", "beta", "BETA");
        Assert.True(hit.Ok, hit.ErrorMessage);
        Assert.Equal("alpha BETA gamma", File.ReadAllText(Path.Combine(_repoRoot, "docs/README.md")));
    }

    [Fact]
    public async Task Move_authorizes_both_endpoints_and_mutates_nothing_when_one_is_denied()
    {
        var (ops, gateway) = CreateOps();
        WriteRepoFile("src/old/Thing.cs", "payload");

        var result = await ops.MoveAsync(_repoRoot, Lease(), "session-a", "src/old/Thing.cs", "src/new/Thing.cs");

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal(
            [("src/old/Thing.cs", "move"), ("src/new/Thing.cs", "move")],
            gateway.Authorizations);
        Assert.False(File.Exists(Path.Combine(_repoRoot, "src/old/Thing.cs")));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(_repoRoot, "src/new/Thing.cs")));

        // Destination denied: source stays in place.
        gateway.Authorizations.Clear();
        gateway.OnAuthorize = (target, _) => target == "src/locked/Destination.cs"
            ? new MutationAuthorizationResult(false, new GatewayError("not_covered", "denied"))
            : null;
        var denied = await ops.MoveAsync(_repoRoot, Lease(), "session-a", "src/new/Thing.cs", "src/locked/Destination.cs");
        Assert.False(denied.Ok);
        Assert.Equal("payload", File.ReadAllText(Path.Combine(_repoRoot, "src/new/Thing.cs")));
        Assert.False(File.Exists(Path.Combine(_repoRoot, "src/locked/Destination.cs")));
    }

    [Fact]
    public async Task Delete_removes_only_after_authorization()
    {
        var (ops, gateway) = CreateOps();
        var path = WriteRepoFile("src/Temp.cs", "x");
        gateway.OnAuthorize = (_, _) => new MutationAuthorizationResult(
            false, new GatewayError("stale_fencing_token", "token expired"));

        var denied = await ops.DeleteAsync(_repoRoot, Lease(), "session-a", "src/Temp.cs");
        Assert.False(denied.Ok);
        Assert.Equal("stale_fencing_token", denied.ErrorCode);
        Assert.True(File.Exists(path));

        gateway.OnAuthorize = null;
        var allowed = await ops.DeleteAsync(_repoRoot, Lease(), "session-a", "src/Temp.cs");
        Assert.True(allowed.Ok, allowed.ErrorMessage);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Read_requires_authorization_and_returns_the_content()
    {
        var (ops, gateway) = CreateOps();
        WriteRepoFile("src/App/A.cs", "content");

        var result = await ops.ReadTextAsync(_repoRoot, Lease(), "session-a", "src/App/A.cs");
        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal("content", ReservedFileOperations.ReadContent(result));
        Assert.Equal([("src/App/A.cs", "read")], gateway.Authorizations);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../../outside.cs")]
    [InlineData("src/../..//escape.cs")]
    [InlineData(".git/config")]
    [InlineData("src/git/../../.git/HEAD")]
    [InlineData("C:/Windows/system32/config")]
    [InlineData("src/back\\slash.cs")]
    public async Task The_path_policy_rejects_targets_outside_the_canonical_workspace(string relativePath)
    {
        var (ops, gateway) = CreateOps();

        var result = await ops.WriteTextAsync(_repoRoot, Lease(), "session-a", relativePath, "boom");

        Assert.False(result.Ok);
        Assert.Empty(gateway.Authorizations);
    }

    [Fact]
    public async Task A_symlink_pointing_outside_the_repository_is_rejected()
    {
        var (ops, _) = CreateOps();
        var outside = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "pi-cc-reserved-ops-out", Guid.NewGuid().ToString("N")));
        try
        {
            Directory.CreateDirectory(Path.Combine(_repoRoot, "src"));
            var linkPath = Path.Combine(_repoRoot, "src", "escape.cs");
            File.CreateSymbolicLink(linkPath, Path.Combine(outside.FullName, "victim.txt"));

            var result = await ops.WriteTextAsync(_repoRoot, Lease(), "session-a", "src/escape.cs", "boom");

            Assert.False(result.Ok);
            Assert.False(File.Exists(Path.Combine(outside.FullName, "victim.txt")));
        }
        finally
        {
            Directory.Delete(outside.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task A_symlink_alias_inside_the_repository_is_rejected_before_io()
    {
        var (ops, gateway) = CreateOps();
        WriteRepoFile("src/real.cs", "canonical");
        var linkPath = Path.Combine(_repoRoot, "alias.cs");
        File.CreateSymbolicLink(linkPath, Path.Combine(_repoRoot, "src", "real.cs"));

        var result = await ops.WriteTextAsync(_repoRoot, Lease(), "session-a", "alias.cs", "boom");

        Assert.Equal("path_symlink_alias", result.ErrorCode);
        Assert.Empty(gateway.Authorizations);
        Assert.Equal("canonical", File.ReadAllText(Path.Combine(_repoRoot, "src", "real.cs")));
    }

    [Fact]
    public async Task A_dangling_symlink_leaf_is_rejected_before_io()
    {
        var (ops, gateway) = CreateOps();
        var outside = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "pi-cc-reserved-ops-out", Guid.NewGuid().ToString("N")));
        try
        {
            Directory.CreateDirectory(Path.Combine(_repoRoot, "src"));
            File.CreateSymbolicLink(
                Path.Combine(_repoRoot, "src", "dangling.cs"),
                Path.Combine(outside.FullName, "not-there.txt"));

            var result = await ops.WriteTextAsync(_repoRoot, Lease(), "session-a", "src/dangling.cs", "boom");

            Assert.False(result.Ok);
            Assert.Empty(gateway.Authorizations);
            Assert.Empty(Directory.GetFiles(outside.FullName));
        }
        finally
        {
            Directory.Delete(outside.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task A_non_existing_leaf_is_written_through_canonical_existing_ancestors()
    {
        var (ops, gateway) = CreateOps();
        Directory.CreateDirectory(Path.Combine(_repoRoot, "src", "deep"));

        var result = await ops.WriteTextAsync(_repoRoot, Lease(), "session-a", "src/deep/new.cs", "created");

        Assert.True(result.Ok, result.ErrorMessage);
        Assert.Equal([("src/deep/new.cs", "write")], gateway.Authorizations);
        Assert.Equal("created", File.ReadAllText(Path.Combine(_repoRoot, "src", "deep", "new.cs")));
    }
}
