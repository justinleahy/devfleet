using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Projects;
using PiCommandCenter.Infrastructure.Requests;

namespace PiCommandCenter.Infrastructure.Tests;

/// <summary>
/// Shared helpers: isolated temporary directories, real temporary Git repositories
/// (via <see cref="ProcessStartInfo.ArgumentList"/>, never the user's repositories), and
/// SQLite-backed <see cref="ControlPlaneDbContext"/> instances. Every call site gets its own
/// unique directory/file so tests stay deterministic and parallel-safe.
/// </summary>
public static class TestRepositories
{
    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pi-cc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates a unique directory and initializes a fresh real Git repository inside it.</summary>
    public static string InitGitRepository(string? parent = null)
    {
        var root = parent ?? CreateTempDirectory();
        var path = Path.Combine(root, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        RunGit(["init", "-q", "-b", "main", path]);
        RunGit(["-C", path, "config", "user.email", "tests@example.invalid"]);
        RunGit(["-C", path, "config", "user.name", "Command Center Tests"]);
        RunGit(["-C", path, "config", "commit.gpgsign", "false"]);
        return path;
    }

    public static void CommitAll(string repositoryPath, string message = "initial")
    {
        RunGit(["-C", repositoryPath, "add", "-A"]);
        RunGit(["-C", repositoryPath, "commit", "-q", "--allow-empty", "-m", message]);
    }

    private static void RunGit(IReadOnlyList<string> argumentList)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in argumentList)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
        {
            Assert.Fail($"git {string.Join(' ', argumentList)} failed with exit code {process.ExitCode}.");
        }
    }

    public static string CreateSqliteFile()
    {
        var path = Path.Combine(CreateTempDirectory(), "controlplane.db");
        // Touch the file so the connection string is unambiguous.
        using (File.Create(path))
        {
        }

        return path;
    }

    public static ControlPlaneDbContext CreateContext(string sqlitePath, bool createSchema = true)
    {
        var connection = new SqliteConnection($"Data Source={sqlitePath}");
        connection.Open();
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlite(connection)
            .Options;
        var context = new ControlPlaneDbContext(options);
        if (createSchema)
        {
            context.Database.EnsureCreated();
        }

        return context;
    }

    public static ProjectCatalog CreateCatalog(ControlPlaneDbContext context, params IReadOnlyList<string> approvedRoots) =>
        new(
            TimeProvider.System,
            context,
            Options.Create(new ProjectCatalogOptions { ApprovedRoots = approvedRoots }),
            new ProjectionNotifier());

    public static RequestQueue CreateQueue(ControlPlaneDbContext context) =>
        new(TimeProvider.System, context, new ProjectionNotifier());

    public static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}
