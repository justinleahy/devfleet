namespace PiCommandCenter.Web.Components.Coordination;

/// <summary>
/// One selectable destination for human guidance (SPEC §16.5): the root session, one specific
/// child, or every active agent of the request. <see cref="Recipients"/> holds the session ids
/// the message is actually addressed to, so the composer never has to re-derive session
/// semantics from labels.
/// </summary>
/// <param name="Value">Stable select value; unique within one target list.</param>
/// <param name="Label">Operator-facing label.</param>
/// <param name="Recipients">Recipient session ids resolved when the list was built.</param>
public sealed record GuidanceTarget(string Value, string Label, IReadOnlyList<string> Recipients);
