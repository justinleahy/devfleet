using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// Boots the real control plane against an isolated temporary SQLite database and an isolated
/// temporary approved root, so tests never touch the user's projects and stay parallel-safe.
/// </summary>
public sealed class ControlPlaneFixture : IDisposable
{
    private readonly string _tempRoot;

    public ControlPlaneFixture()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "pi-cc-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        ApprovedRoot = Path.Combine(_tempRoot, "approved");
        Directory.CreateDirectory(ApprovedRoot);
        SqlitePath = Path.Combine(_tempRoot, "controlplane.db");
        using (File.Create(SqlitePath))
        {
        }

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={SqlitePath}");
            builder.UseSetting("Projects:ApprovedRoots:0", ApprovedRoot);
        });

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        db.Database.Migrate();
    }

    public WebApplicationFactory<Program> Factory { get; }

    public string ApprovedRoot { get; }

    public string SqlitePath { get; }

    public HttpClient CreateClient() => Factory.CreateClient();

    /// <summary>Initializes a fresh real Git repository inside the fixture's approved root.</summary>
    public string CreateGitRepository()
    {
        var path = Path.Combine(ApprovedRoot, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        RunGit(["init", "-q", "-b", "main", path]);
        RunGit(["-C", path, "config", "user.email", "tests@example.invalid"]);
        RunGit(["-C", path, "config", "user.name", "Command Center Tests"]);
        return path;
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

    public void Dispose()
    {
        Factory.Dispose();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
