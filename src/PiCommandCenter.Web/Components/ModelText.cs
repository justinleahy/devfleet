namespace PiCommandCenter.Web.Components;

/// <summary>
/// Operator-facing model formatting shared by the fleet views. A session carries both a runtime
/// kind and a canonical <c>&lt;runtime&gt;/&lt;model&gt;</c> selector, and the selector normally
/// repeats the kind verbatim — so the rule for when the kind still earns its own place on screen
/// lives here rather than in each view.
/// </summary>
public static class ModelText
{
    /// <summary>
    /// The runtime kind to show alongside the model selector, or <c>null</c> when there is nothing
    /// to add — either the selector already names the runtime, or no runtime was reported.
    /// </summary>
    public static string? RuntimeAside(string runtime, string model) =>
        string.IsNullOrWhiteSpace(runtime) || SelectorNamesRuntime(runtime, model) ? null : runtime;

    private static bool SelectorNamesRuntime(string runtime, string model) =>
        runtime.Length > 0
        && model.Length > runtime.Length
        && model[runtime.Length] == '/'
        && model.AsSpan(0, runtime.Length).Equals(runtime, StringComparison.OrdinalIgnoreCase);
}
