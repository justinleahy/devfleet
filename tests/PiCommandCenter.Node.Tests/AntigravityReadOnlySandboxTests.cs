using System.Diagnostics;
using PiCommandCenter.Node.Runtime.Antigravity;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Proves the Antigravity bwrap boundary masks cross-provider OAuth stores while preserving
/// writable host-native Antigravity state. Temporary paths exercise the real mount behavior
/// without provider network or credentials.
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
    public void Production_paths_use_the_current_users_provider_stores()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".gemini"), AntigravityReadOnlySandbox.StateLocation);
        Assert.Equal(
            [
                "/provider-auth",
                Path.Combine(home, ".pi", "agent"),
                Path.Combine(home, ".claude"),
                Path.Combine(home, ".config", "muse"),
            ],
            AntigravityReadOnlySandbox.MaskedSecretLocations);
        Assert.DoesNotContain(AntigravityReadOnlySandbox.StateLocation, AntigravityReadOnlySandbox.MaskedSecretLocations);
    }

    [Fact]
    public void Argv_places_masks_after_every_bind_so_no_later_mount_shadows_them()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var providerAuth = Directory.CreateDirectory(Path.Combine(_root, "provider-auth")).FullName;
        var piAgent = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".pi", "agent")).FullName;
        var claudeHome = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".claude")).FullName;
        var museConfig = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".config", "muse")).FullName;
        var geminiStore = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".gemini")).FullName;
        var psi = new ProcessStartInfo { FileName = "agy" };
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");

        AntigravityReadOnlySandbox.Apply(
            psi,
            repo,
            "/usr/bin/bwrap",
            [providerAuth, piAgent, claudeHome, museConfig],
            geminiStore);

        Assert.Equal("/usr/bin/bwrap", psi.FileName);
        string[] argv = [.. psi.ArgumentList];
        Assert.Equal(
            [
                "--die-with-parent", "--new-session", "--unshare-pid",
                "--ro-bind", "/", "/",
                "--dev", "/dev",
                "--proc", "/proc",
                "--ro-bind", repo, repo,
                "--bind", geminiStore, geminiStore,
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
        var missingGeminiStore = Path.Combine(_root, "home", "node", ".gemini");
        var psi = new ProcessStartInfo { FileName = "agy" };

        AntigravityReadOnlySandbox.Apply(
            psi,
            repo,
            "/usr/bin/bwrap",
            [providerAuth, missingPiAgent, missingClaudeHome, missingMuseConfig],
            missingGeminiStore);

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
    public void Writable_state_overlapping_repository_fails_closed()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(repo, ".gemini")).FullName;

        AssertWritableStateOverlapFails(repo, state);
    }

    [Fact]
    public void Writable_state_with_a_symlinked_ancestor_targeting_repository_fails_closed()
    {
        var repo = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        Directory.CreateDirectory(Path.Combine(repo, ".gemini"));
        var stateAlias = Directory.CreateSymbolicLink(Path.Combine(_root, "state-alias"), repo).FullName;

        AssertWritableStateOverlapFails(repo, Path.Combine(stateAlias, ".gemini"));
    }

    [Fact]
    public void Repository_with_a_symlinked_ancestor_targeting_writable_state_fails_closed()
    {
        var state = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        Directory.CreateDirectory(Path.Combine(state, "repo"));
        var repoAlias = Directory.CreateSymbolicLink(Path.Combine(_root, "repo-alias"), state).FullName;

        AssertWritableStateOverlapFails(Path.Combine(repoAlias, "repo"), state);
    }

    private static void AssertWritableStateOverlapFails(string repo, string state)
    {
        var psi = new ProcessStartInfo { FileName = "agy" };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AntigravityReadOnlySandbox.Apply(
                psi,
                repo,
                "/usr/bin/bwrap",
                maskedLocations: [],
                writableStateLocation: state));

        Assert.Contains("BLOCKED", ex.Message, StringComparison.Ordinal);
        Assert.Contains("overlaps", ex.Message, StringComparison.Ordinal);
        Assert.Equal("agy", psi.FileName);
    }

    [Fact]
    public async Task Masked_stores_are_empty_while_gemini_state_is_writable_and_repository_stays_read_only()
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
        var geminiState = Directory.CreateDirectory(Path.Combine(_root, "home", "node", ".gemini")).FullName;
        var geminiCliStore = Directory.CreateDirectory(Path.Combine(geminiState, "antigravity-cli")).FullName;
        var geminiToken = Path.Combine(geminiCliStore, "token");
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
            printf writable > "${10}/antigravity-cli/write-test"
            printf forbidden > "${11}/write-test" 2>/dev/null && exit 19
            cat "${10}/antigravity-cli/token" "${11}/README.md" "${10}/antigravity-cli/write-test"
            """);
        psi.ArgumentList.Add("sh");
        string[] arguments =
        [
            providerAuthSecret, piSecret, claudeSecret, museSecret, leakedSecret,
            providerAuth, piAgent, claudeHome, museConfig, geminiState, repo,
        ];
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        AntigravityReadOnlySandbox.Apply(
            psi,
            repo,
            maskedLocations: [providerAuth, piAgent, claudeHome, museConfig],
            writableStateLocation: geminiState);

        using var process = Process.Start(psi);
        Assert.NotNull(process);
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"exit {process.ExitCode}: {stderr}");
        Assert.Contains("gemini-token-visible", stdout, StringComparison.Ordinal);
        Assert.Contains("writable", stdout, StringComparison.Ordinal);
        Assert.Contains("repo-visible", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", stdout, StringComparison.Ordinal);
    }
}
