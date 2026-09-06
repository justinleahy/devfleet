using System.Diagnostics.CodeAnalysis;

namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// Canonical <c>&lt;provider&gt;/&lt;model&gt;</c> selector, e.g. <c>codex/gpt-5.6-sol</c>,
/// <c>zai/glm-4.7</c>, or <c>claude-code/fable-5-1</c>. The provider prefix picks the host-owned
/// adapter: the reserved official-harness providers (<see cref="OfficialHarnessProviders"/>) route
/// to their own adapters and every other syntactically valid provider uses the Pi runtime
/// (<see cref="UsesPiRuntime"/>). The model id after the first <c>/</c> is handed to that provider
/// verbatim, may itself contain slashes, and must name an explicit provider-native model.
/// </summary>
public sealed record AgentModelSelector
{
    public const int MaxLength = 256;

    public const string Codex = "codex";
    public const string ClaudeCode = "claude-code";
    public const string Antigravity = "antigravity";
    public const string Muse = "muse";

    /// <summary>Provider prefix rejected because Pi is a runtime, not a provider.</summary>
    public const string Pi = "pi";

    /// <summary>Pi SDK provider id the <see cref="Codex"/> selector provider maps onto.</summary>
    public const string PiCodexProvider = "openai-codex";

    /// <summary>
    /// Reserved provider prefixes that route to an official-harness adapter instead of Pi.
    /// Every other valid provider uses the Pi runtime.
    /// </summary>
    public static readonly IReadOnlyList<string> OfficialHarnessProviders = [ClaudeCode, Antigravity, Muse];

    private AgentModelSelector(string value, string provider, string modelId)
    {
        Value = value;
        Provider = provider;
        ModelId = modelId;
    }

    /// <summary>Trimmed canonical form, <c>Provider/ModelId</c>.</summary>
    public string Value { get; }

    /// <summary>
    /// Provider prefix; a lowercase ASCII alphanumeric slug with interior hyphens, never <c>pi</c>.
    /// </summary>
    public string Provider { get; }

    /// <summary>Provider-native model id, everything after the first <c>/</c>.</summary>
    public string ModelId { get; }

    /// <summary>True when this provider is served by the Pi runtime adapter.</summary>
    public bool UsesPiRuntime => !OfficialHarnessProviders.Contains(Provider, StringComparer.Ordinal);

    /// <summary>
    /// Pi SDK provider id for a Pi-backed selector: <c>codex</c> maps to <c>openai-codex</c>;
    /// every other Pi provider maps identically. Meaningful only when <see cref="UsesPiRuntime"/>.
    /// </summary>
    public string PiProviderId => Provider == Codex ? PiCodexProvider : Provider;

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
            error = "Model selector must be '<provider>/<model>'.";
            return false;
        }

        var provider = clean[..slash];
        var modelId = clean[(slash + 1)..];
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(modelId))
        {
            error = "Model selector must name both a provider and a model.";
            return false;
        }

        if (modelId == "default")
        {
            error = "Model selector must name an explicit model; model id 'default' is not allowed.";
            return false;
        }

        if (!IsValidProviderSlug(provider))
        {
            error = $"Provider '{provider}' must be a lowercase ASCII alphanumeric slug with interior hyphens.";
            return false;
        }

        if (provider == Pi)
        {
            error = $"Provider '{Pi}' is not allowed: Pi is a runtime, not a provider.";
            return false;
        }

        selector = new AgentModelSelector(clean, provider, modelId);
        error = null;
        return true;
    }

    public override string ToString() => Value;

    private static bool IsValidProviderSlug(string provider)
    {
        var previousWasHyphen = true; // rejects a leading hyphen
        foreach (var c in provider)
        {
            if (c == '-')
            {
                if (previousWasHyphen)
                {
                    return false;
                }

                previousWasHyphen = true;
                continue;
            }

            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c))
            {
                return false;
            }

            previousWasHyphen = false;
        }

        return !previousWasHyphen; // rejects a trailing hyphen
    }
}
