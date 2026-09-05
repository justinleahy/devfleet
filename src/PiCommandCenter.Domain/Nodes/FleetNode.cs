using System.Text.Json;

namespace PiCommandCenter.Domain.Nodes;

/// <summary>
/// A node that hosts projects and connects to the Control Plane. Constructed only through
/// <see cref="FleetNode.Register"/> or rehydration via <see cref="FleetNode.Rehydrate"/> so
/// invalid state is unrepresentable.
/// </summary>
public sealed class FleetNode
{
    private static readonly JsonSerializerOptions CapabilitiesOptions = new(JsonSerializerDefaults.Web);

    private FleetNode(
        NodeId id,
        string displayName,
        string agentVersion,
        NodeStatus status,
        DateTimeOffset lastHeartbeatAt,
        string capabilitiesJson,
        string? resourceSnapshotJson,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        Id = id;
        DisplayName = displayName;
        AgentVersion = agentVersion;
        Status = status;
        LastHeartbeatAt = lastHeartbeatAt;
        CapabilitiesJson = capabilitiesJson;
        ResourceSnapshotJson = resourceSnapshotJson;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Version = version;
    }

    public NodeId Id { get; }

    /// <summary>Normalized non-empty display name.</summary>
    public string DisplayName { get; private set; }

    /// <summary>Normalized non-empty agent version reported by the node.</summary>
    public string AgentVersion { get; private set; }

    public NodeStatus Status { get; private set; }

    public DateTimeOffset LastHeartbeatAt { get; private set; }

    /// <summary>Non-empty JSON document describing node capabilities.</summary>
    public string CapabilitiesJson { get; private set; }
    public string? ResourceSnapshotJson { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency token.</summary>
    public long Version { get; private set; }

    /// <summary>
    /// Registers a new node in the <see cref="NodeStatus.Offline"/> state; the node goes Online on
    /// its first heartbeat. Throws <see cref="ArgumentException"/> when any invariant is violated.
    /// </summary>
    public static FleetNode Register(
        NodeId id,
        string displayName,
        string agentVersion,
        string capabilitiesJson,
        DateTimeOffset at)
    {
        var (display, version, capabilities) = Normalize(displayName, agentVersion, capabilitiesJson);

        return new FleetNode(
            id,
            display,
            version,
            NodeStatus.Offline,
            lastHeartbeatAt: at,
            capabilities,
            resourceSnapshotJson: null,
            createdAt: at,
            updatedAt: at,
            version: 1);
    }

    /// <summary>Rehydrates a persisted node without mutating timestamps or version.</summary>
    public static FleetNode Rehydrate(
        NodeId id,
        string displayName,
        string agentVersion,
        NodeStatus status,
        DateTimeOffset lastHeartbeatAt,
        string capabilitiesJson,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version,
        string? resourceSnapshotJson = null)
    {
        var (display, versionText, capabilities) = Normalize(displayName, agentVersion, capabilitiesJson);

        return new FleetNode(
            id,
            display,
            versionText,
            status,
            lastHeartbeatAt,
            capabilities,
            resourceSnapshotJson,
            createdAt,
            updatedAt,
            version);
    }

    /// <summary>
    /// Applies a heartbeat: the node becomes <see cref="NodeStatus.Online"/>, timestamps advance,
    /// identity metadata is refreshed, and the latest resource snapshot is replaced.
    /// </summary>
    public void Heartbeat(
        string agentVersion,
        string capabilitiesJson,
        DateTimeOffset at,
        string? resourceSnapshotJson = null)
    {
        var (_, version, capabilities) = Normalize(DisplayName, agentVersion, capabilitiesJson);

        AgentVersion = version;
        CapabilitiesJson = capabilities;
        ResourceSnapshotJson = resourceSnapshotJson;
        Status = NodeStatus.Online;
        LastHeartbeatAt = at;
        UpdatedAt = at;
        Version++;
    }

    /// <summary>
    /// Applies a re-registration from a reconnecting node: identity metadata (including the
    /// display name) is refreshed, the node becomes <see cref="NodeStatus.Online"/>, timestamps
    /// advance and the concurrency version increments atomically. Throws
    /// <see cref="ArgumentException"/> on invalid input without mutating any state.
    /// </summary>
    public void RefreshRegistration(string displayName, string agentVersion, string capabilitiesJson, DateTimeOffset at)
    {
        var (display, version, capabilities) = Normalize(displayName, agentVersion, capabilitiesJson);

        DisplayName = display;
        AgentVersion = version;
        CapabilitiesJson = capabilities;
        Status = NodeStatus.Online;
        LastHeartbeatAt = at;
        UpdatedAt = at;
        Version++;
    }

    /// <summary>
    /// Marks the node offline after a missed heartbeat window. Idempotent: an already offline node
    /// is left untouched.
    /// </summary>
    public void MarkOffline(DateTimeOffset at)
    {
        if (Status == NodeStatus.Offline)
        {
            return;
        }

        Status = NodeStatus.Offline;
        UpdatedAt = at;
        Version++;
    }

    private static (string Display, string Version, string Capabilities) Normalize(
        string displayName,
        string agentVersion,
        string capabilitiesJson)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name must not be empty.", nameof(displayName));
        }

        var display = displayName.Trim();
        if (display.Length == 0)
        {
            throw new ArgumentException("Display name must not be empty.", nameof(displayName));
        }

        if (string.IsNullOrWhiteSpace(agentVersion))
        {
            throw new ArgumentException("Agent version must not be empty.", nameof(agentVersion));
        }

        var version = agentVersion.Trim();
        if (version.Length == 0)
        {
            throw new ArgumentException("Agent version must not be empty.", nameof(agentVersion));
        }

        if (string.IsNullOrWhiteSpace(capabilitiesJson))
        {
            throw new ArgumentException("Capabilities must be a non-empty JSON document.", nameof(capabilitiesJson));
        }

        var capabilities = capabilitiesJson.Trim();
        try
        {
            _ = JsonSerializer.Deserialize<JsonDocument>(capabilities, CapabilitiesOptions)
                ?? throw new ArgumentException("Capabilities must be a non-empty JSON document.", nameof(capabilitiesJson));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Capabilities must be a well-formed JSON document.", nameof(capabilitiesJson), ex);
        }

        return (display, version, capabilities);
    }
}
