using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Node.Runtime.Antigravity;
using PiCommandCenter.Node.Runtime.Claude;

namespace PiCommandCenter.Node.Runtime;

/// <summary>
/// Fixed profile → adapter map. Unknown profiles are rejected; agents cannot pick executables.
/// </summary>
public sealed class AgentRuntimeRegistry : IAgentRuntimeRegistry
{
    private readonly PiRuntimeAdapter _pi;
    private readonly ClaudeCodeRuntimeAdapter _claude;
    private readonly AntigravityRuntimeAdapter _antigravity;

    public AgentRuntimeRegistry(
        PiRuntimeAdapter pi,
        ClaudeCodeRuntimeAdapter claude,
        AntigravityRuntimeAdapter antigravity)
    {
        _pi = pi ?? throw new ArgumentNullException(nameof(pi));
        _claude = claude ?? throw new ArgumentNullException(nameof(claude));
        _antigravity = antigravity ?? throw new ArgumentNullException(nameof(antigravity));
    }

    public IAgentRuntimeAdapter Resolve(string runtimeProfile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeProfile);
        return runtimeProfile.Trim() switch
        {
            AgentRuntimeProfiles.LocalPi => _pi,
            AgentRuntimeProfiles.ClaudeReadOnly or AgentRuntimeProfiles.ClaudeReservedWrite => _claude,
            AgentRuntimeProfiles.AntigravityReadOnly => _antigravity,
            _ => throw new NotSupportedException(
                $"Runtime profile '{runtimeProfile}' is not in the trusted allowlist."),
        };
    }
}
