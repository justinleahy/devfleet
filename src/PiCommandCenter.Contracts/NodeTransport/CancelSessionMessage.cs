namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Control-plane → node command to cancel one agent session (SPEC §23.2 / §30.3). The hub
/// pushes this to the cancelled session's live group, so exactly the node currently heartbeating
/// that session active receives it. The receiving runtime must stop the session's process and
/// report the outcome by publishing a real <c>session.cancelled</c> event — the control-plane
/// projection follows the node event and is never synthesized from the request alone.
/// </summary>
public sealed record CancelSessionCommand(
    string SessionId,
    string Reason);
