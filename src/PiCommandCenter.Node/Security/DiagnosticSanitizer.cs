using System.Text.RegularExpressions;

namespace PiCommandCenter.Node.Security;

/// <summary>
/// Bounded diagnostic redaction for stderr tails and malformed runtime lines (SPEC §34.6).
/// Never intended for wholesale environment dumps.
/// </summary>
public static partial class DiagnosticSanitizer
{
    public const int DefaultMaxChars = 4096;

    public static string Sanitize(string? text, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var value = text;
        if (LooksLikeEnvironmentDump(value))
        {
            value = "[redacted-environment-dump]";
        }
        else
        {
            value = JsonSecretFields().Replace(value, "\"$1\":\"[redacted]\"");
            value = BearerToken().Replace(value, "Bearer [redacted]");
            value = Jwt().Replace(value, "[redacted-jwt]");
            value = AnthropicKey().Replace(value, "[redacted-api-key]");
            value = GenericSkKey().Replace(value, "[redacted-api-key]");
            value = GoogleApiKey().Replace(value, "[redacted-api-key]");
            value = SlackToken().Replace(value, "[redacted-token]");
            value = GitHubPat().Replace(value, "[redacted-token]");
            value = UnixHomePath().Replace(value, "[redacted-path]");
            value = WindowsUserPath().Replace(value, "[redacted-path]");
        }

        if (maxChars > 0 && value.Length > maxChars)
        {
            return value[..maxChars] + "…";
        }

        return value;
    }

    public static string SanitizeLine(string? line, int maxChars = DefaultMaxChars)
        => Sanitize(line, maxChars);

    private static bool LooksLikeEnvironmentDump(string value)
        => value.Contains("Environment variables", StringComparison.OrdinalIgnoreCase)
           || value.Contains("GetEnvironmentVariables", StringComparison.Ordinal)
           || (value.Contains("PATH=", StringComparison.Ordinal)
               && value.Contains("HOME=", StringComparison.Ordinal)
               && value.Contains("USER=", StringComparison.Ordinal));

    [GeneratedRegex(
        "(?i)\"(password|passwd|token|api[_-]?key|secret|authorization|credential|access_token|refresh_token|bearer)\"\\s*:\\s*\"(?:\\\\.|[^\"\\\\])*\"",
        RegexOptions.CultureInvariant)]
    private static partial Regex JsonSecretFields();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"eyJ[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex AnthropicKey();

    [GeneratedRegex(@"sk-[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex GenericSkKey();

    [GeneratedRegex(@"AIza[0-9A-Za-z\-_]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex(@"xox[baprs]-[A-Za-z0-9-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"ghp_[A-Za-z0-9]{20,}", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubPat();

    [GeneratedRegex(@"/(?:home|Users)/[^\s:""']+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixHomePath();

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\s:""']+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsUserPath();
}
