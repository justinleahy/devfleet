namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// Resolves a trusted runtime profile to a host-owned adapter. Profiles are an exact
/// allowlist; adapters are never selected by version or agent-supplied paths.
/// </summary>
public interface IAgentRuntimeRegistry
{
    IAgentRuntimeAdapter Resolve(string runtimeProfile);
}
