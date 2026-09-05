using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PiCommandCenter.Node.Runtime.Claude.Hooks;

/// <summary>Paths of one host-owned Claude settings install (outside the repository).</summary>
public sealed record ClaudeHookInstallResult(
    string SettingsPath,
    string HookPath,
    string RootDirectory,
    string ValidatorUrl);

/// <summary>
/// Writes application-owned Claude settings and an executable PreToolUse/PostToolUse hook
/// under a private data directory. <c>--settings</c> outranks project/user files; Unix modes
/// keep the files owner-only so a repository cannot replace the gate.
/// </summary>
public sealed class ClaudeHookSettingsInstaller
{
    private readonly ClaudeReservationHookServer _server;
    private readonly string _root;

    public ClaudeHookSettingsInstaller(ClaudeReservationHookServer server, string? dataRoot = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _root = dataRoot
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "pi-command-center",
                "claude-runtime");
    }

    public ClaudeHookInstallResult Install(string profile, ClaudeHookSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (profile is not (ClaudeRuntimeProfiles.ReadOnly or ClaudeRuntimeProfiles.ReservedWrite))
        {
            throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown Claude runtime profile.");
        }

        _server.EnsureStarted();
        _server.Register(context);

        var sessionDir = Path.Combine(_root, Sanitize(context.SessionId));
        NodeOptionsPostConfigure.CreatePrivateDirectory(sessionDir);
        RestrictOwnerOnly(sessionDir, executable: true);

        var hookPath = Path.Combine(sessionDir, "reservation-hook");
        var settingsPath = Path.Combine(sessionDir, "settings.json");
        File.WriteAllText(hookPath, HookScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        RestrictOwnerOnly(hookPath, executable: true);

        var validatorUrl = _server.BaseUrl;
        var settings = BuildSettings(profile, hookPath, validatorUrl, context.SessionId);
        File.WriteAllText(settingsPath, settings, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        RestrictOwnerOnly(settingsPath, executable: false);

        return new ClaudeHookInstallResult(settingsPath, hookPath, sessionDir, validatorUrl);
    }

    public void Uninstall(string sessionId)
    {
        _server.Unregister(sessionId);
    }

    internal static string BuildSettings(
        string profile,
        string hookPath,
        string validatorUrl,
        string sessionId)
    {
        var write = profile == ClaudeRuntimeProfiles.ReservedWrite;
        var allow = write
            ? new[] { "Read", "Glob", "Grep", "Edit", "Write" }
            : new[] { "Read", "Glob", "Grep" };
        var preCommand = $"{Quote(hookPath)} pre {Quote(validatorUrl)} {Quote(sessionId)}";
        var postCommand = $"{Quote(hookPath)} post {Quote(validatorUrl)} {Quote(sessionId)}";

        var payload = new Dictionary<string, object?>
        {
            ["permissions"] = new Dictionary<string, object?>
            {
                ["defaultMode"] = "dontAsk",
                ["allow"] = allow,
                ["deny"] = new[] { "Bash", "PowerShell" },
            },
            ["hooks"] = new Dictionary<string, object?>
            {
                ["PreToolUse"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["matcher"] = "Bash|PowerShell",
                        ["hooks"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "command",
                                ["command"] = preCommand,
                                ["timeout"] = 5000,
                            },
                        },
                    },
                    new Dictionary<string, object?>
                    {
                        ["matcher"] = "Edit|Write",
                        ["hooks"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "command",
                                ["command"] = preCommand,
                                ["timeout"] = 5000,
                            },
                        },
                    },
                },
                ["PostToolUse"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["matcher"] = "Edit|Write",
                        ["hooks"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "command",
                                ["command"] = postCommand,
                                ["timeout"] = 5000,
                            },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
    }

    internal const string HookScript =
        """
        #!/bin/sh
        set -eu
        mode="${1:?}"
        base="${2:?}"
        session="${3:?}"
        body="$(mktemp)"
        out="$(mktemp)"
        trap 'rm -f "$body" "$out"' EXIT
        cat > "$body"
        url="${base}/${mode}?sessionId=${session}"
        code="$(curl -sS -o "$out" -w '%{http_code}' --max-time 2 \
          -H 'Content-Type: application/json' --data-binary @"$body" "$url" || echo 000)"
        if [ "$code" = "200" ]; then
          cat "$out"
          exit 0
        fi
        cat "$out" >&2 || true
        exit 2
        """;

    private static string Quote(string value)
        => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Sanitize(string sessionId)
    {
        var chars = sessionId.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray();
        var name = new string(chars);
        return name.Length == 0 ? "session" : name;
    }

    internal static void RestrictOwnerOnly(string path, bool executable)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (executable)
        {
            mode |= UnixFileMode.UserExecute;
        }

        File.SetUnixFileMode(path, mode);
    }

    /// <summary>Binds an ephemeral loopback TCP port so HttpListener need not use port 0.</summary>
    internal static int AllocateLoopbackPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
