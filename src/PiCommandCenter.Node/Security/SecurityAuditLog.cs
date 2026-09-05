using Microsoft.Extensions.Logging;

namespace PiCommandCenter.Node.Security;

/// <summary>Structured audit lines for cancellation, force-release, completion override, and Git mutations (SPEC §34.6).</summary>
public static class SecurityAuditLog
{
    public static void Cancellation(ILogger logger, string sessionId, string actor, string reason)
        => logger.LogInformation(
            "AUDIT cancel session={SessionId} actor={Actor} reason={Reason}",
            sessionId,
            actor,
            DiagnosticSanitizer.Sanitize(reason, 256));

    public static void ForceRelease(ILogger logger, Guid leaseId, string actor, string reason)
        => logger.LogInformation(
            "AUDIT force-release lease={LeaseId} actor={Actor} reason={Reason}",
            leaseId,
            actor,
            DiagnosticSanitizer.Sanitize(reason, 256));

    public static void CompletionOverride(ILogger logger, Guid requestId, string actor, int overriddenFindings)
        => logger.LogInformation(
            "AUDIT completion-override request={RequestId} actor={Actor} findings={Count}",
            requestId,
            actor,
            overriddenFindings);

    public static void GitMutation(ILogger logger, string repositoryRoot, string command, string actor)
        => logger.LogInformation(
            "AUDIT git-mutation repo={Repo} command={Command} actor={Actor}",
            repositoryRoot,
            command,
            actor);
}
