using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Projects;

namespace PiCommandCenter.Node.Tests;

public sealed class WorkspaceDirectoryBrowserTests : IDisposable
{
    private readonly string _testRoot = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "pi-cc-workspace-browse", Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _approvedRoot;

    public WorkspaceDirectoryBrowserTests()
    {
        _approvedRoot = Directory.CreateDirectory(Path.Combine(_testRoot, "approved")).FullName;
    }

    [Fact]
    public async Task Null_path_lists_existing_approved_roots_without_current_or_parent()
    {
        var browser = CreateBrowser();

        var result = await browser.BrowseAsync(new WorkspaceDirectoryBrowseRequestMessage(null));

        Assert.Null(result.ErrorCode);
        Assert.Null(result.CurrentPath);
        Assert.Null(result.ParentPath);
        var entry = Assert.Single(result.Directories);
        Assert.Equal(Canonical(_approvedRoot), entry.Path);
        Assert.Equal(Path.GetFileName(Canonical(_approvedRoot)), entry.Name);
    }

    [Fact]
    public async Task Null_path_omits_missing_roots_and_deduplicates()
    {
        var missing = Path.Combine(_testRoot, "missing-root");
        var browser = CreateBrowser(missing, _approvedRoot, _approvedRoot + Path.DirectorySeparatorChar);

        var result = await browser.BrowseAsync(new WorkspaceDirectoryBrowseRequestMessage(null));

        Assert.Null(result.ErrorCode);
        var entry = Assert.Single(result.Directories);
        Assert.Equal(Canonical(_approvedRoot), entry.Path);
    }

    [Fact]
    public async Task Directory_lists_sorted_direct_child_directories_only()
    {
        Directory.CreateDirectory(Path.Combine(_approvedRoot, "zeta"));
        Directory.CreateDirectory(Path.Combine(_approvedRoot, "alpha"));
        Directory.CreateDirectory(Path.Combine(_approvedRoot, "mu"));
        Directory.CreateDirectory(Path.Combine(_approvedRoot, "alpha", "nested"));
        File.WriteAllText(Path.Combine(_approvedRoot, "note.txt"), "not a directory");

        var result = await CreateBrowser().BrowseAsync(new WorkspaceDirectoryBrowseRequestMessage(_approvedRoot));

        Assert.Null(result.ErrorCode);
        Assert.Equal(Canonical(_approvedRoot), result.CurrentPath);
        Assert.Null(result.ParentPath);
        Assert.Equal(["alpha", "mu", "zeta"], result.Directories.Select(d => d.Name).ToArray());
        Assert.All(result.Directories, d => Assert.Equal(Canonical(_approvedRoot), Path.GetDirectoryName(d.Path)));
    }

    [Fact]
    public async Task Child_directory_reports_canonical_parent()
    {
        var child = Directory.CreateDirectory(Path.Combine(_approvedRoot, "child")).FullName;

        var result = await CreateBrowser().BrowseAsync(
            new WorkspaceDirectoryBrowseRequestMessage(child + Path.DirectorySeparatorChar + "."));

        Assert.Null(result.ErrorCode);
        Assert.Equal(Canonical(child), result.CurrentPath);
        Assert.Equal(Canonical(_approvedRoot), result.ParentPath);
    }

    [Fact]
    public async Task Path_outside_approved_roots_is_rejected()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_testRoot, "outside")).FullName;

        var result = await CreateBrowser().BrowseAsync(new WorkspaceDirectoryBrowseRequestMessage(outside));

        AssertError(result, WorkspaceDirectoryBrowseErrorCodes.OutsideApprovedRoot);
    }

    [Fact]
    public async Task Relative_and_control_character_paths_are_rejected()
    {
        var browser = CreateBrowser();

        AssertError(
            await browser.BrowseAsync(new WorkspaceDirectoryBrowseRequestMessage("relative/path")),
            WorkspaceDirectoryBrowseErrorCodes.InvalidPath);
        AssertError(
            await browser.BrowseAsync(new WorkspaceDirectoryBrowseRequestMessage(_approvedRoot + "\0")),
            WorkspaceDirectoryBrowseErrorCodes.InvalidPath);
    }

    [Fact]
    public async Task Missing_path_is_reported()
    {
        var result = await CreateBrowser().BrowseAsync(
            new WorkspaceDirectoryBrowseRequestMessage(Path.Combine(_approvedRoot, "missing")));

        AssertError(result, WorkspaceDirectoryBrowseErrorCodes.PathMissing);
    }

    [Fact]
    public async Task Symlinked_traversal_path_is_rejected()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_testRoot, "outside")).FullName;
        var alias = Path.Combine(_approvedRoot, "alias");
        Directory.CreateSymbolicLink(alias, outside);

        var result = await CreateBrowser().BrowseAsync(
            new WorkspaceDirectoryBrowseRequestMessage(Path.Combine(alias, "child")));

        AssertError(result, WorkspaceDirectoryBrowseErrorCodes.OutsideApprovedRoot);
    }

    [Fact]
    public async Task Approved_root_below_symlinked_ancestor_is_omitted_from_root_list()
    {
        var real = Directory.CreateDirectory(Path.Combine(_testRoot, "real-ancestor")).FullName;
        var alias = Path.Combine(_testRoot, "alias-ancestor");
        Directory.CreateSymbolicLink(alias, real);
        var belowAlias = Path.Combine(alias, "sub");
        var browser = CreateBrowser(belowAlias, _approvedRoot);

        var result = await browser.BrowseAsync(new WorkspaceDirectoryBrowseRequestMessage(null));

        Assert.Null(result.ErrorCode);
        var entry = Assert.Single(result.Directories);
        Assert.Equal(Canonical(_approvedRoot), entry.Path);
    }

    [Fact]
    public async Task Approved_root_below_symlinked_ancestor_cannot_be_browsed()
    {
        var real = Directory.CreateDirectory(Path.Combine(_testRoot, "real-ancestor")).FullName;
        var alias = Path.Combine(_testRoot, "alias-ancestor");
        Directory.CreateSymbolicLink(alias, real);
        var belowAlias = Path.Combine(alias, "sub");
        Directory.CreateDirectory(belowAlias);

        var result = await CreateBrowser(belowAlias).BrowseAsync(
            new WorkspaceDirectoryBrowseRequestMessage(belowAlias));

        AssertError(result, WorkspaceDirectoryBrowseErrorCodes.OutsideApprovedRoot);
    }

    [Fact]
    public async Task Traversal_through_a_file_is_reported_as_path_missing()
    {
        var file = Path.Combine(_approvedRoot, "not-a-directory");
        await File.WriteAllTextAsync(file, "x");

        var result = await CreateBrowser().BrowseAsync(
            new WorkspaceDirectoryBrowseRequestMessage(Path.Combine(file, "child")));

        AssertError(result, WorkspaceDirectoryBrowseErrorCodes.PathMissing);
    }

    [Fact]
    public async Task Symlink_children_are_omitted()
    {
        var real = Directory.CreateDirectory(Path.Combine(_approvedRoot, "real")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(_approvedRoot, "link"), real);

        var result = await CreateBrowser().BrowseAsync(new WorkspaceDirectoryBrowseRequestMessage(_approvedRoot));

        Assert.Null(result.ErrorCode);
        var entry = Assert.Single(result.Directories);
        Assert.Equal("real", entry.Name);
    }

    [Fact]
    public async Task Results_are_bounded_to_five_hundred_entries()
    {
        for (var index = 0; index < 520; index++)
        {
            Directory.CreateDirectory(Path.Combine(_approvedRoot, $"child-{index:D4}"));
        }

        var result = await CreateBrowser().BrowseAsync(new WorkspaceDirectoryBrowseRequestMessage(_approvedRoot));

        Assert.Null(result.ErrorCode);
        Assert.Equal(500, result.Directories.Count);
        Assert.Equal(
            result.Directories.Select(d => d.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            result.Directories.Select(d => d.Name).ToArray());
    }

    private WorkspaceDirectoryBrowser CreateBrowser(params string[] approvedRoots)
    {
        var roots = approvedRoots.Length > 0 ? approvedRoots : [_approvedRoot];
        return new WorkspaceDirectoryBrowser(Options.Create(new WorkspaceValidationOptions
        {
            ApprovedRoots = roots,
        }));
    }

    private static string Canonical(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void AssertError(WorkspaceDirectoryBrowseResponseMessage result, string expectedCode)
    {
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.NotNull(result.ErrorDetail);
        Assert.True(result.ErrorDetail.Length <= 512);
        Assert.Empty(result.Directories);
        Assert.Null(result.CurrentPath);
        Assert.Null(result.ParentPath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
