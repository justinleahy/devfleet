using System.Diagnostics;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Opt-in compatibility probes against installed official CLIs. Explicitly skipped unless
/// the matching <c>RUN_REAL_*</c> environment variable is set at discovery time. Records CLI
/// version only; never spends quota or performs model calls.
/// </summary>
public sealed class RealRuntimeContractTests
{
    public static bool ClaudeOptIn => IsEnabled("RUN_REAL_CLAUDE_TESTS");

    public static bool AgyOptIn => IsEnabled("RUN_REAL_ANTIGRAVITY_TESTS");

    public static bool MuseOptIn => IsEnabled("RUN_REAL_MUSE_TESTS");

    [Fact]
    public void Claude_cli_version_is_recorded_when_opted_in()
    {
        if (!ClaudeOptIn)
        {
            return; // opt-in only: RUN_REAL_CLAUDE_TESTS=1
        }

        var version = CaptureVersion("claude", "--version");
        Assert.False(string.IsNullOrWhiteSpace(version));
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "pi-cc-claude-cli-version.txt"),
            version);
    }

    [Fact]
    public void Antigravity_cli_version_is_recorded_when_opted_in()
    {
        if (!AgyOptIn)
        {
            return; // opt-in only: RUN_REAL_ANTIGRAVITY_TESTS=1
        }

        var version = CaptureVersion("agy", "--version");
        Assert.False(string.IsNullOrWhiteSpace(version));
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "pi-cc-agy-cli-version.txt"),
            version);
    }

    [Fact]
    public void Muse_cli_version_is_recorded_when_opted_in()
    {
        if (!MuseOptIn)
        {
            return; // opt-in only: RUN_REAL_MUSE_TESTS=1
        }

        var version = CaptureVersion("muse", "--version");
        Assert.False(string.IsNullOrWhiteSpace(version));
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "pi-cc-muse-cli-version.txt"),
            version);
    }

    private static bool IsEnabled(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.Equals(value, "1", StringComparison.Ordinal)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string CaptureVersion(string fileName, string argument)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            ArgumentList = { argument },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} failed to start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(10));
        return (stdout + stderr).Trim();
    }
}
