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
    public void Production_masks_cover_claude_pi_provider_auth_and_muse_but_keep_gemini()
    {
        string[] masks = [.. AntigravityReadOnlySandbox.MaskedSecretLocations];
        Assert.Equal(
            [
                "/provider-auth",
                "/home/node/.pi/agent",
                "/home/node/.claude",
                "/home/node/.config/muse",
            ],
            masks);
        Assert.DoesNotContain("/home/node/.gemini", masks);
    }

    [Fact]
    public void Argv_places_masks_after_every_bind_so_no_later_mount_shadows_them()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var providerAuth = Directory.CreateDirectory(Path.Combine(_root, "provider-auth")).FullName;
        var piAgent = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".pi", "agent")).FullName;
        var claudeHome = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".claude")).FullName;
        var museConfig = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".config", "muse")).FullName;
        var psi = new ProcessStartInfo { FileName = "agy" };
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");

        AntigravityReadOnlySandbox.Apply(
            psi,
            repo,
            "/usr/bin/bwrap",
            [providerAuth, piAgent, claudeHome, museConfig]);

        Assert.Equal("/usr/bin/bwrap", psi.FileName);
        string[] argv = [.. psi.ArgumentList];
        Assert.Equal(
            [
                "--die-with-parent", "--new-session", "--unshare-pid",
                "--ro-bind", "/", "/",
                "--dev", "/dev",
                "--proc", "/proc",
                "--ro-bind", repo, repo,
                "--tmpfs", providerAuth,
                "--tmpfs", piAgent,
                "--tmpfs", claudeHome,
                "--tmpfs", museConfig,
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
        var providerAuth = Directory.CreateDirectory(Path.Combine(_root, "provider-auth")).FullName;
        var missingPiAgent = Path.Combine(_root, "home", "node", ".pi", "agent");
        var missingClaudeHome = Path.Combine(_root, "home", "node", ".claude");
        var missingMuseConfig = Path.Combine(_root, "home", "node", ".config", "muse");
        var psi = new ProcessStartInfo { FileName = "agy" };

        AntigravityReadOnlySandbox.Apply(
            psi,
            repo,
            "/usr/bin/bwrap",
            [providerAuth, missingPiAgent, missingClaudeHome, missingMuseConfig]);

        string[] argv = [.. psi.ArgumentList];
        Assert.Equal(
            [
                "--die-with-parent", "--new-session", "--unshare-pid",
                "--ro-bind", "/", "/",
                "--dev", "/dev",
                "--proc", "/proc",
                "--ro-bind", repo, repo,
                "--tmpfs", providerAuth,
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
        var museConfig = Directory.CreateDirectory(Path.Combine(_root, ".config", "muse")).FullName;
        var repo = Directory.CreateDirectory(Path.Combine(museConfig, "projects", "ws")).FullName;
        var psi = new ProcessStartInfo { FileName = "agy" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AntigravityReadOnlySandbox.Apply(psi, repo, "/usr/bin/bwrap", [museConfig]));

        Assert.Contains("BLOCKED", ex.Message, StringComparison.Ordinal);
        Assert.Contains(museConfig, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Masked_stores_are_empty_while_gemini_store_and_repository_stay_readable()
    {
        var providerAuth = Directory.CreateDirectory(Path.Combine(_root, "provider-auth")).FullName;
        var providerAuthSecret = Path.Combine(providerAuth, "pi-auth.json");
        await File.WriteAllTextAsync(providerAuthSecret, "{\"refresh_token\":\"provider-auth-secret\"}");
        var piAgent = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".pi", "agent")).FullName;
        var piSecret = Path.Combine(piAgent, "auth.json");
        await File.WriteAllTextAsync(piSecret, "{\"refresh_token\":\"pi-secret\"}");
        var claudeHome = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".claude")).FullName;
        var claudeSecret = Path.Combine(claudeHome, ".credentials.json");
        await File.WriteAllTextAsync(claudeSecret, "{\"refresh_token\":\"claude-secret\"}");
        var museConfig = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".config", "muse")).FullName;
        var museSecret = Path.Combine(museConfig, "oauth.json");
        await File.WriteAllTextAsync(museSecret, "{\"refresh_token\":\"muse-secret\"}");
        var geminiStore = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".gemini", "antigravity-cli")).FullName;
        var geminiToken = Path.Combine(geminiStore, "token");
        await File.WriteAllTextAsync(geminiToken, "gemini-token-visible");
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var readme = Path.Combine(repo, "README.md");
        await File.WriteAllTextAsync(readme, "repo-visible");
        Directory.CreateSymbolicLink(Path.Combine(repo, "leak"), piAgent);
        var leakedSecret = Path.Combine(repo, "leak", "auth.json");

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
            test -e "$4" && exit 13
            test -e "$5" && exit 14
            [ -z "$(ls -A "$6")" ] || exit 15
            [ -z "$(ls -A "$7")" ] || exit 16
            [ -z "$(ls -A "$8")" ] || exit 17
            [ -z "$(ls -A "$9")" ] || exit 18
            cat "${10}" "${11}"
            """);
        psi.ArgumentList.Add("sh");
        string[] arguments =
        [
            providerAuthSecret, piSecret, claudeSecret, museSecret, leakedSecret,
            providerAuth, piAgent, claudeHome, museConfig, geminiToken, readme,
        ];
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        AntigravityReadOnlySandbox.Apply(
            psi,
            repo,
            maskedLocations: [providerAuth, piAgent, claudeHome, museConfig]);

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
