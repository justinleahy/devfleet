using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain.Sessions;

namespace PiCommandCenter.Node.Security;

/// <summary>
/// Classifies official-CLI authentication failure as blocked input-required, not a generic crash
/// (SPEC §33.4). Reasons name the provider-native local login and never collect credentials.
/// </summary>
public static class ProviderAuthClassifier
{
    public static bool IsMissing(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return false;
        }

        var text = diagnostic;
        return Contains(text, "not logged in")
               || Contains(text, "please log in")
               || Contains(text, "please login")
               || Contains(text, "authentication required")
               || Contains(text, "not authenticated")
               || Contains(text, "unauthenticated")
               || Contains(text, "claude login")
               || Contains(text, "agy login")
               || Contains(text, "antigravity login")
               || Contains(text, "gcloud auth")
               || Contains(text, "run `claude")
               || Contains(text, "invalid api key")
               || Contains(text, "missing api key")
               || Contains(text, "api key not found")
               || Contains(text, "oauth") && Contains(text, "login");
    }

    public static string NativeLoginReason(string runtimeKind)
    {
        if (string.Equals(runtimeKind, AgentRuntimeKinds.ClaudeCode, StringComparison.Ordinal))
        {
            return "Complete Claude Code login locally (claude login). The Command Center does not collect provider credentials.";
        }

        if (string.Equals(runtimeKind, AgentRuntimeKinds.Antigravity, StringComparison.Ordinal))
        {
            return "Complete Antigravity login locally (agy login). The Command Center does not collect provider credentials.";
        }

        return "Complete provider-native login locally. The Command Center does not collect provider credentials.";
    }

    public static Dictionary<string, object?> SnapshotPayload(string runtimeKind, string? diagnostic)
    {
        var reason = NativeLoginReason(runtimeKind);
        return new Dictionary<string, object?>
        {
            ["attention"] = nameof(AgentAttention.InputRequired),
            ["workState"] = nameof(AgentWorkState.Blocked),
            ["activity"] = nameof(AgentActivity.Idle),
            ["liveness"] = nameof(AgentLiveness.Online),
            ["statusReason"] = reason,
            ["reason"] = reason,
            ["auth"] = "provider_native_login_required",
            ["diagnostic"] = DiagnosticSanitizer.Sanitize(diagnostic, 512),
        };
    }

    private static bool Contains(string haystack, string needle)
        => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
