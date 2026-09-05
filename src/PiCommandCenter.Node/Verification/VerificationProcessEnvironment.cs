using System.Collections;
using System.Diagnostics;

namespace PiCommandCenter.Node.Verification;

/// <summary>
/// Builds an explicit, minimal process environment for verification: no inherited
/// provider/admin/node secrets, and HOME/config/cache redirected into a private temp sandbox.
/// </summary>
public static class VerificationProcessEnvironment
{
    public const string SandboxPrefix = "pi-cc-verify-sandbox";

    private static readonly HashSet<string> SecretNameFragments = new(StringComparer.OrdinalIgnoreCase)
    {
        "SECRET", "TOKEN", "PASSWORD", "PASSWD", "CREDENTIAL", "API_KEY", "APIKEY",
        "ACCESS_KEY", "PRIVATE_KEY", "AUTH", "BEARER", "SESSION",
    };

    private static readonly HashSet<string> SecretNameExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN",
        "CLAUDE_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN",
        "OPENAI_API_KEY", "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_APPLICATION_CREDENTIALS",
        "AWS_SECRET_ACCESS_KEY", "AWS_ACCESS_KEY_ID", "AWS_SESSION_TOKEN",
        "AZURE_CLIENT_SECRET", "GH_TOKEN", "GITHUB_TOKEN", "NPM_TOKEN", "NODE_AUTH_TOKEN",
        "PI_API_KEY", "DOTNET_ROOT",
    };

    public static string CreateSandbox()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), SandboxPrefix, Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(root, "home"));
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        return root;
    }

    public static void ApplyMinimal(ProcessStartInfo startInfo, string sandboxRoot)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(sandboxRoot);

        startInfo.Environment.Clear();

        var home = Path.Combine(sandboxRoot, "home");
        var cache = Path.Combine(sandboxRoot, "cache");
        var config = Path.Combine(sandboxRoot, "config");
        var data = Path.Combine(sandboxRoot, "data");
        var tmp = Path.Combine(sandboxRoot, "tmp");

        startInfo.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        startInfo.Environment["HOME"] = home;
        startInfo.Environment["USERPROFILE"] = home;
        startInfo.Environment["TMPDIR"] = tmp;
        startInfo.Environment["TMP"] = tmp;
        startInfo.Environment["TEMP"] = tmp;
        startInfo.Environment["XDG_CACHE_HOME"] = cache;
        startInfo.Environment["XDG_CONFIG_HOME"] = config;
        startInfo.Environment["XDG_DATA_HOME"] = data;
        startInfo.Environment["XDG_STATE_HOME"] = data;
        startInfo.Environment["XDG_RUNTIME_DIR"] = tmp;
        startInfo.Environment["DOTNET_CLI_HOME"] = home;
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["NUGET_PACKAGES"] = Path.Combine(cache, "nuget");
        startInfo.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(cache, "nuget-http");
        startInfo.Environment["NPM_CONFIG_CACHE"] = Path.Combine(cache, "npm");
        startInfo.Environment["TERM"] = "dumb";
        startInfo.Environment["LANG"] = Environment.GetEnvironmentVariable("LANG") ?? "C.UTF-8";
    }

    public static bool IsSecretName(string name)
    {
        if (SecretNameExact.Contains(name))
        {
            return true;
        }

        foreach (var fragment in SecretNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<string> CollectHostSecretValues()
    {
        var values = new List<string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string name || entry.Value is not string value)
            {
                continue;
            }

            if (value.Length >= 4 && IsSecretName(name))
            {
                values.Add(value);
            }
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrEmpty(home) && home.Length >= 4)
        {
            values.Add(home);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile) && profile.Length >= 4)
        {
            values.Add(profile);
        }

        return values;
    }

    public static void TryDeleteSandbox(string? sandboxRoot)
    {
        if (string.IsNullOrWhiteSpace(sandboxRoot))
        {
            return;
        }

        try
        {
            if (Directory.Exists(sandboxRoot))
            {
                Directory.Delete(sandboxRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
