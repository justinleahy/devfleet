using System.Text.Json;
using PiCommandCenter.Node.Child;

namespace PiCommandCenter.Node.Runtime.Claude.Hooks;

/// <summary>Outcome of one PreToolUse / PostToolUse hook evaluation.</summary>
public sealed record ClaudeHookDecision(
    bool Allow,
    int SuggestedExitCode,
    string Reason,
    string StdoutJson)
{
    public static ClaudeHookDecision Deny(string reason, int exitCode = 2)
        => new(false, exitCode, reason, DenyJson(reason));

    public static ClaudeHookDecision Permit()
        => new(true, 0, "allow", AllowJson());

    public static string DenyJson(string reason)
        => "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":"
           + JsonSerializer.Serialize(reason)
           + "}}";

    public static string AllowJson()
        => "{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"allow\"}}";
}

/// <summary>
/// Fail-closed PreToolUse gate for Claude Edit/Write and a hard deny for Bash/PowerShell.
/// Lease id, fencing token, and session id come from <see cref="ClaudeHookSessionContext"/>,
/// never from tool_input. <c>file_path</c> must be an absolute path inside the repository.
/// </summary>
public sealed class ClaudeReservationHookEvaluator
{
    public static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromSeconds(2);

    private readonly INodeReservationGateway _reservations;
    private readonly ClaudeHookAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationTokenSource> _timeoutFactory;

    public ClaudeReservationHookEvaluator(
        INodeReservationGateway reservations,
        ClaudeHookAuditLog audit,
        TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationTokenSource>? timeoutFactory = null)
    {
        _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _time = timeProvider ?? TimeProvider.System;
        _timeoutFactory = timeoutFactory ?? (timeout => new CancellationTokenSource(timeout));
    }

    public async Task<ClaudeHookDecision> EvaluatePreAsync(
        string stdinJson,
        ClaudeHookSessionContext? context,
        CancellationToken cancellationToken = default)
    {
        if (context is null
            || string.IsNullOrWhiteSpace(context.SessionId)
            || context.LeaseId == Guid.Empty
            || string.IsNullOrWhiteSpace(context.RepositoryRoot))
        {
            return ClaudeHookDecision.Deny("missing trusted session context");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdinJson);
        }
        catch (JsonException)
        {
            return ClaudeHookDecision.Deny("malformed hook input");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ClaudeHookDecision.Deny("malformed hook input");
            }

            var toolName = ReadToolName(document.RootElement);
            if (IsShellTool(toolName))
            {
                return ClaudeHookDecision.Deny("Bash and PowerShell are denied", exitCode: 2);
            }

            if (!IsMutationTool(toolName, out var operation))
            {
                return ClaudeHookDecision.Deny($"tool '{toolName}' is not permitted");
            }

            if (!TryReadFilePath(document.RootElement, out var filePath))
            {
                return ClaudeHookDecision.Deny("missing absolute file_path");
            }

            if (!TryRepositoryRelative(context.RepositoryRoot, filePath, out var relative, out var pathError))
            {
                return ClaudeHookDecision.Deny(pathError);
            }

            MutationAuthorizationResult authorization;
            try
            {
                using var timeout = _timeoutFactory(AuthorizationTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, timeout.Token);
                authorization = await _reservations.AuthorizeAsync(
                    context.LeaseId,
                    context.FencingToken,
                    context.SessionId,
                    relative,
                    operation,
                    linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return ClaudeHookDecision.Deny("reservation authorization timed out");
            }
            catch (Exception ex)
            {
                return ClaudeHookDecision.Deny(ex.Message);
            }

            if (!authorization.Authorized)
            {
                var code = authorization.Error?.Code ?? "denied";
                var message = authorization.Error?.Message ?? "reservation denied";
                return ClaudeHookDecision.Deny($"{code}: {message}");
            }

            return ClaudeHookDecision.Permit();
        }
    }

    public ClaudeHookDecision EvaluatePost(string stdinJson, ClaudeHookSessionContext? context)
    {
        var sessionId = context?.SessionId ?? "";
        var toolName = "";
        var path = "";
        var operation = "";
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdinJson) ? "{}" : stdinJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                toolName = ReadToolName(document.RootElement);
                TryReadFilePath(document.RootElement, out path);
                if (IsMutationTool(toolName, out var op))
                {
                    operation = op;
                }
            }
        }
        catch (JsonException)
        {
            // PostToolUse cannot block; still record a bounded event.
        }

        _audit.Record(new ClaudeHookAuditEvent(
            _time.GetUtcNow(),
            sessionId,
            toolName,
            path,
            operation));
        return new ClaudeHookDecision(true, 0, "recorded", "{}");
    }

    internal static bool IsShellTool(string toolName)
        => toolName.Equals("Bash", StringComparison.OrdinalIgnoreCase)
            || toolName.Equals("PowerShell", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMutationTool(string toolName, out string operation)
    {
        if (toolName.Equals("Write", StringComparison.OrdinalIgnoreCase))
        {
            operation = "write";
            return true;
        }

        if (toolName.Equals("Edit", StringComparison.OrdinalIgnoreCase))
        {
            operation = "edit";
            return true;
        }

        operation = "";
        return false;
    }

    internal static bool TryRepositoryRelative(
        string repositoryRoot,
        string absolutePath,
        out string relative,
        out string error)
    {
        relative = "";
        error = "";
        if (string.IsNullOrWhiteSpace(absolutePath)
            || !Path.IsPathRooted(absolutePath)
            || absolutePath.StartsWith('~'))
        {
            error = "file_path must be absolute";
            return false;
        }

        string resolved;
        try
        {
            var root = Path.GetFullPath(repositoryRoot);
            var full = Path.GetFullPath(absolutePath);
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootPrefix, StringComparison.Ordinal)
                && !string.Equals(full, root, StringComparison.Ordinal))
            {
                error = "outside: path is outside the repository";
                return false;
            }

            var rel = Path.GetRelativePath(root, full).Replace('\\', '/');
            resolved = RepositoryPathPolicy.Resolve(root, rel);
            relative = Path.GetRelativePath(root, resolved).Replace('\\', '/');
            return true;
        }
        catch (RepositoryPathPolicyException ex)
        {
            error = $"outside: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"outside: {ex.Message}";
            return false;
        }
    }

    private static string ReadToolName(JsonElement root)
        => root.TryGetProperty("tool_name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? ""
            : "";

    private static bool TryReadFilePath(JsonElement root, out string path)
    {
        path = "";
        if (!root.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!input.TryGetProperty("file_path", out var file) || file.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        path = file.GetString() ?? "";
        return path.Length > 0;
    }
}
