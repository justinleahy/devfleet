namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>Stable runtime readiness statuses.</summary>
public static class RuntimeReadinessStatuses
{
    public const string Ready = "ready";
    public const string Unavailable = "unavailable";
    public const string Unknown = "unknown";
}

/// <summary>Stable identifiers for shared runtime readiness evidence.</summary>
public static class RuntimeReadinessEvidenceSources
{
    public const string UnsupportedNativeObservation = "unsupported_native_observation";
    public const string RuntimeAdapterProbe = "runtime_adapter_probe";
}

/// <summary>Adapter-observed readiness for one routed role and canonical model.</summary>
public sealed record RuntimeRouteReadinessMessage(
    string Role,
    string CanonicalModel,
    string Readiness,
    string EvidenceSource,
    DateTimeOffset ObservedAt,
    string RoutingRevision);

/// <summary>Node execution capacity and runtime readiness reported with a heartbeat.</summary>
public sealed record NodeExecutionStatusMessage(
    DateTimeOffset ObservedAt,
    int AvailableRequestSlots,
    IReadOnlyList<Guid> ActiveAssignmentIds,
    string RoutingRevision,
    IReadOnlyList<RuntimeRouteReadinessMessage> Routes);
