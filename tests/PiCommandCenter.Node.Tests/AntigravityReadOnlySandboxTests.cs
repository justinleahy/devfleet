using System.Diagnostics;
using PiCommandCenter.Node.Runtime.Antigravity;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Proves the Antigravity bwrap boundary masks cross-provider OAuth stores. Production
/// locations cannot be created on the test host, so argv tests substitute temporary
/// directories through the <c>maskedLocations</c> seam; the constants themselves are asserted
/// separately. No provider network or credentials.
/// </summary>
[Collection("Antigravity process tests")]
public sealed class AntigravityReadOnlySandboxTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pi-cc-agy-sandbox-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Production_masks_cover_pi_and_claude_stores_and_keep_gemini()
    {
        string[] masks = [.. AntigravityReadOnlySandbox.MaskedSecretLocations];
        Assert.Equal(["/provider-auth", "/home/node/.claude"], masks);
        Assert.DoesNotContain("/home/node/.gemini", masks);
    }

    [Fact]
    public void Argv_places_masks_after_every_bind_so_no_later_mount_shadows_them()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var piStore = Directory.CreateDirectory(Path.Combine(_root, "provider-auth")).FullName;
        var claudeHome = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".claude")).FullName;
        var psi = new ProcessStartInfo { FileName = "agy" };
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");

        AntigravityReadOnlySandbox.Apply(psi, repo, "/usr/bin/bwrap", [piStore, claudeHome]);

        Assert.Equal("/usr/bin/bwrap", psi.FileName);
        string[] argv = [.. psi.ArgumentList];
        Assert.Equal(
            [
                "--die-with-parent", "--new-session", "--unshare-pid",
                "--ro-bind", "/", "/",
                "--dev", "/dev",
                "--proc", "/proc",
                "--ro-bind", repo, repo,
                "--tmpfs", piStore,
                "--tmpfs", claudeHome,
                "--chdir", repo,
                "--", "agy", "--output-format", "stream-json",
            ],
            argv);

        var firstMask = Array.IndexOf(argv, "--tmpfs");
        var lastMask = Array.LastIndexOf(argv, "--tmpfs");
        var lastMount = argv
            .Select((token, index) => (token, index))
            .Where(pair => pair.token is "--ro-bind" or "--bind" or "--dev" or "--proc" or "--dir" or "--symlink")
            .Max(pair => pair.index);
        Assert.True(lastMount < firstMask, "a mount after a mask could re-expose the secret store");
        Assert.Equal(["--chdir", repo, "--"], argv[(lastMask + 2)..(lastMask + 5)]);
    }

    [Fact]
    public void Absent_secret_locations_emit_no_mask()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var missing = Path.Combine(_root, "provider-auth");
        var psi = new ProcessStartInfo { FileName = "agy" };

        AntigravityReadOnlySandbox.Apply(psi, repo, "/usr/bin/bwrap", [missing]);

        string[] argv = [.. psi.ArgumentList];
        Assert.Equal(
            [
                "--die-with-parent", "--new-session", "--unshare-pid",
                "--ro-bind", "/", "/",
                "--dev", "/dev",
                "--proc", "/proc",
                "--ro-bind", repo, repo,
                "--chdir", repo,
                "--", "agy",
            ],
            argv);
    }

    [Fact]
    public void Secret_location_that_is_not_a_directory_fails_closed()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var secretFile = Path.Combine(_root, "provider-auth");
        File.WriteAllText(secretFile, "{}");
        var psi = new ProcessStartInfo { FileName = "agy" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AntigravityReadOnlySandbox.Apply(psi, repo, "/usr/bin/bwrap", [secretFile]));

        Assert.Contains("BLOCKED", ex.Message, StringComparison.Ordinal);
        Assert.Contains(secretFile, ex.Message, StringComparison.Ordinal);
        Assert.Equal("agy", psi.FileName);
    }

    [Fact]
    public void Repository_inside_masked_location_fails_closed()
    {
        var claudeHome = Directory.CreateDirectory(Path.Combine(_root, ".claude")).FullName;
        var repo = Directory.CreateDirectory(Path.Combine(claudeHome, "projects", "ws")).FullName;
        var psi = new ProcessStartInfo { FileName = "agy" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AntigravityReadOnlySandbox.Apply(psi, repo, "/usr/bin/bwrap", [claudeHome]));

        Assert.Contains("BLOCKED", ex.Message, StringComparison.Ordinal);
        Assert.Contains(claudeHome, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Masked_stores_are_empty_while_gemini_store_and_repository_stay_readable()
    {
        var piStore = Directory.CreateDirectory(Path.Combine(_root, "provider-auth")).FullName;
        var piSecret = Path.Combine(piStore, "pi-auth.json");
        await File.WriteAllTextAsync(piSecret, "{\"refresh_token\":\"pi-secret\"}");
        var claudeHome = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".claude")).FullName;
        var claudeSecret = Path.Combine(claudeHome, ".credentials.json");
        await File.WriteAllTextAsync(claudeSecret, "{\"refresh_token\":\"claude-secret\"}");
        var geminiStore = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".gemini", "antigravity-cli")).FullName;
        var geminiToken = Path.Combine(geminiStore, "token");
        await File.WriteAllTextAsync(geminiToken, "gemini-token-visible");
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var readme = Path.Combine(repo, "README.md");
        await File.WriteAllTextAsync(readme, "repo-visible");
        Directory.CreateSymbolicLink(Path.Combine(repo, "leak"), piStore);
        var leakedSecret = Path.Combine(repo, "leak", "pi-auth.json");

        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(
            """
            test -e "$1" && exit 10
            test -e "$2" && exit 11
            test -e "$3" && exit 12
            [ -z "$(ls -A "$4")" ] || exit 13
            [ -z "$(ls -A "$5")" ] || exit 14
            cat "$6" "$7"
            """);
        psi.ArgumentList.Add("sh");
        foreach (var argument in new[] { piSecret, claudeSecret, leakedSecret, piStore, claudeHome, geminiToken, readme })
        {
            psi.ArgumentList.Add(argument);
        }

        AntigravityReadOnlySandbox.Apply(psi, repo, maskedLocations: [piStore, claudeHome]);

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {stderr}");
        Assert.Contains("gemini-token-visible", stdout, StringComparison.Ordinal);
        Assert.Contains("repo-visible", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", stdout, StringComparison.Ordinal);
    }
}
