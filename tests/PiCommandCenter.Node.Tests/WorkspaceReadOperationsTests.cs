using PiCommandCenter.Node.Child;

namespace PiCommandCenter.Node.Tests;

public sealed class WorkspaceReadOperationsTests : IDisposable
{
    private readonly string _repo = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pi-cc-ws-read", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Read_returns_in_repo_content_and_rejects_escape()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "a.ts"), "hello");

        var ok = WorkspaceReadOperations.Read(_repo, "src/a.ts");
        Assert.True(ok.Ok);
        Assert.Equal("hello", Assert.IsType<ReadResult>(ok).Content);

        var escape = WorkspaceReadOperations.Read(_repo, "../secret");
        Assert.False(escape.Ok);
        Assert.Equal("path_traversal", escape.ErrorCode);
    }

    [Fact]
    public void Grep_find_ls_stay_inside_the_repository()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "a.ts"), "alpha token");
        File.WriteAllText(Path.Combine(_repo, "src", "b.md"), "other");

        var grep = Assert.IsType<ReadResult>(WorkspaceReadOperations.Grep(_repo, "src", "token"));
        Assert.Contains("src/a.ts", grep.Content);

        var find = Assert.IsType<ReadResult>(WorkspaceReadOperations.Find(_repo, ".", "*.ts"));
        Assert.Contains("src/a.ts", find.Content);
        Assert.DoesNotContain("b.md", find.Content);

        var list = Assert.IsType<ReadResult>(WorkspaceReadOperations.List(_repo, "src"));
        Assert.Contains("src/a.ts", list.Content);
    }

    [Fact]
    public void Symlink_escape_is_not_readable()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pi-cc-ws-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "classified");
        var link = Path.Combine(_repo, "leak");
        File.CreateSymbolicLink(link, Path.Combine(outside, "secret.txt"));

        var read = WorkspaceReadOperations.Read(_repo, "leak");
        Assert.False(read.Ok);

        var grep = WorkspaceReadOperations.Grep(_repo, ".", "classified");
        Assert.True(grep.Ok);
        Assert.DoesNotContain("classified", Assert.IsType<ReadResult>(grep).Content ?? "");
        Assert.DoesNotContain("secret", Assert.IsType<ReadResult>(grep).Content ?? "");

        try
        {
            Directory.Delete(outside, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
