namespace PiCommandCenter.Domain.Nodes;

/// <summary>
/// Connection status of a fleet node as observed by the Control Plane.
/// </summary>
public enum NodeStatus
{
    Offline = 0,
    Online = 1,
}
