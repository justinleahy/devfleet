using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Node.Runtime.Antigravity;
using PiCommandCenter.Node.Runtime.Claude;
using PiCommandCenter.Node.Runtime.Muse;

namespace PiCommandCenter.Node.Runtime;

/// <summary>
/// Routes by the selector's provider prefix: reserved official-harness providers map to their
/// adapters and every other valid provider resolves to Pi. Selectors fail closed at parse time,
/// so agents can never pick executables.
/// </summary>
public sealed class AgentRuntimeRegistry : IAgentRuntimeRegistry
{
    private readonly PiRuntimeAdapter _pi;
    private readonly ClaudeCodeRuntimeAdapter _claude;
    private readonly AntigravityRuntimeAdapter _antigravity;
    private readonly MuseCodeRuntimeAdapter _muse;

    public AgentRuntimeRegistry(
        PiRuntimeAdapter pi,
        ClaudeCodeRuntimeAdapter claude,
        AntigravityRuntimeAdapter antigravity,
        MuseCodeRuntimeAdapter muse)
    {
        _pi = pi ?? throw new ArgumentNullException(nameof(pi));
        _claude = claude ?? throw new ArgumentNullException(nameof(claude));
        _antigravity = antigravity ?? throw new ArgumentNullException(nameof(antigravity));
        _muse = muse ?? throw new ArgumentNullException(nameof(muse));
    }

    public IAgentRuntimeAdapter Resolve(AgentModelSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return selector.Provider switch
        {
            AgentModelSelector.ClaudeCode => _claude,
            AgentModelSelector.Antigravity => _antigravity,
            AgentModelSelector.Muse => _muse,
            _ => _pi,
        };
    }
}
