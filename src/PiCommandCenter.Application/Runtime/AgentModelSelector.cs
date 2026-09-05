using System.Diagnostics.CodeAnalysis;

namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// Canonical <c>&lt;runtime&gt;/&lt;model&gt;</c> selector, e.g. <c>codex/gpt-6-astra</c> or
/// <c>claude-code/fable-5-1</c>. The runtime prefix is the exact trusted allowlist key that picks a
/// host-owned adapter; the model id after the first <c>/</c> is handed to that provider verbatim
/// and may itself contain slashes. The model id <see cref="DefaultModelId"/> asks the provider for
/// its default model.
/// </summary>
public sealed record AgentModelSelector
{
    public const int MaxLength = 256;

    /// <summary>Reserved model id meaning "the provider's default model".</summary>
    public const string DefaultModelId = "default";

    public const string Codex = "codex";
    public const string ClaudeCode = "claude-code";
    public const string Antigravity = "antigravity";
    public const string Muse = "muse";

    /// <summary>Exact runtime prefixes a selector may name.</summary>
    public static readonly IReadOnlyList<string> Runtimes = [Codex, ClaudeCode, Antigravity, Muse];

    private AgentModelSelector(string value, string runtime, string modelId)
    {
        Value = value;
        Runtime = runtime;
        ModelId = modelId;
    }

    /// <summary>Trimmed canonical form, <c>Runtime/ModelId</c>.</summary>
    public string Value { get; }

    /// <summary>Trusted runtime prefix; one of <see cref="Runtimes"/>.</summary>
    public string Runtime { get; }

    /// <summary>Provider-native model id, everything after the first <c>/</c>.</summary>
    public string ModelId { get; }

    public bool IsProviderDefault => ModelId == DefaultModelId;

    /// <summary>Parses a selector; throws <see cref="ArgumentException"/> when it is not canonical.</summary>
    public static AgentModelSelector Parse(string? value)
    {
        if (!TryParse(value, out var selector, out var error))
        {
            throw new ArgumentException(error, nameof(value));
        }

        return selector;
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out AgentModelSelector? selector)
        => TryParse(value, out selector, out _);

    private static bool TryParse(
        string? value,
        [NotNullWhen(true)] out AgentModelSelector? selector,
        [NotNullWhen(false)] out string? error)
    {
        selector = null;

        var clean = value?.Trim();
        if (string.IsNullOrEmpty(clean))
        {
            error = "Model selector must not be empty.";
            return false;
        }

        if (clean.Length > MaxLength)
        {
            error = $"Model selector must not exceed {MaxLength} characters.";
            return false;
        }

        var slash = clean.IndexOf('/');
        if (slash < 0)
        {
            error = "Model selector must be '<runtime>/<model>'.";
            return false;
        }

        var runtime = clean[..slash];
        var modelId = clean[(slash + 1)..];
        if (string.IsNullOrWhiteSpace(runtime) || string.IsNullOrWhiteSpace(modelId))
        {
            error = "Model selector must name both a runtime and a model.";
            return false;
        }

        if (!Runtimes.Contains(runtime, StringComparer.Ordinal))
        {
            error = $"Runtime '{runtime}' is not one of: {string.Join(", ", Runtimes)}.";
            return false;
        }

        selector = new AgentModelSelector(clean, runtime, modelId);
        error = null;
        return true;
    }

    public override string ToString() => Value;
}
