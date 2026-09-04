namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// User or parent input delivered to a running session via
/// <see cref="IAgentRuntimeAdapter.SendAsync"/>.
/// </summary>
public sealed record AgentInput
{
    public AgentInput(string text, IReadOnlyDictionary<string, string>? attachments = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length == 0)
        {
            throw new ArgumentException("Input text must not be empty.", nameof(text));
        }

        Text = text.Trim();
        Attachments = attachments;
    }

    /// <summary>Non-empty instruction text.</summary>
    public string Text { get; }

    /// <summary>Optional named attachments (file paths, references).</summary>
    public IReadOnlyDictionary<string, string>? Attachments { get; }
}
