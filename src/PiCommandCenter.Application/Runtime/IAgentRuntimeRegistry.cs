namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// Resolves a canonical model selector to a host-owned adapter by its trusted runtime prefix.
/// Prefixes are an exact allowlist; adapters are never selected by version or agent-supplied paths.
/// </summary>
public interface IAgentRuntimeRegistry
{
    IAgentRuntimeAdapter Resolve(AgentModelSelector selector);
}
