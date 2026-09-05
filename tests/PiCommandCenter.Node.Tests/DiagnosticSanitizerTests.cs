using PiCommandCenter.Node.Security;

namespace PiCommandCenter.Node.Tests;

public sealed class DiagnosticSanitizerTests
{
    [Fact]
    public void Redacts_bearer_jwt_api_keys_and_json_secret_fields()
    {
        var raw = """
            Bearer abcdef.secret.token
            eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.payload.signature
            sk-ant-secretvalue1234567890abcdef
            {"password":"hunter2","token":"abc","api_key":"xyz","authorization":"nope"}
            AIzaSyAaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
            ghp_abcdefghijklmnopqrstuvwxyz123456
            """;

        var sanitized = DiagnosticSanitizer.Sanitize(raw);
        Assert.DoesNotContain("hunter2", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant-secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef.secret.token", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("AIzaSyA", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_abcdefghijklmnopqrstuvwxyz123456", sanitized, StringComparison.Ordinal);
        Assert.Contains("[redacted]", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Bounds_output_length()
    {
        var huge = new string('a', 20_000);
        var sanitized = DiagnosticSanitizer.Sanitize(huge, maxChars: 128);
        Assert.True(sanitized.Length <= 129);
        Assert.EndsWith("…", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_environment_dumps_instead_of_logging_wholesale()
    {
        var dump = "Environment variables:\nPATH=/usr/bin\nHOME=/home/me\nUSER=me\nSECRET=1";
        var sanitized = DiagnosticSanitizer.Sanitize(dump);
        Assert.Equal("[redacted-environment-dump]", sanitized);
        Assert.DoesNotContain("SECRET=1", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Redacts_unix_and_windows_user_paths()
    {
        var raw = "failed at /home/justin/.config/pi/token.json and C:\\Users\\justin\\AppData\\Local\\pi\\secrets.json";
        var sanitized = DiagnosticSanitizer.Sanitize(raw);
        Assert.DoesNotContain("/home/justin", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users\\justin", sanitized, StringComparison.Ordinal);
        Assert.Contains("[redacted-path]", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Preserves_provider_auth_phrases_for_classification()
    {
        var raw = "Error: not logged in. Run `claude login` at /home/justin/.claude";
        var sanitized = DiagnosticSanitizer.Sanitize(raw);
        Assert.True(ProviderAuthClassifier.IsMissing(sanitized));
        Assert.DoesNotContain("/home/justin", sanitized, StringComparison.Ordinal);
    }
}

