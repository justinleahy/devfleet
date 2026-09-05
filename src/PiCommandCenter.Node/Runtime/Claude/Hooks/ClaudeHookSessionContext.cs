namespace PiCommandCenter.Node.Runtime.Claude.Hooks;

/// <summary>
/// Host-owned reservation identity for one Claude session. Never taken from model-controlled
/// tool input; the installer bakes the session id into the hook command and the node looks
/// the rest up locally.
/// </summary>
public sealed record ClaudeHookSessionContext(
    string SessionId,
    Guid LeaseId,
    long FencingToken,
    string RepositoryRoot);
