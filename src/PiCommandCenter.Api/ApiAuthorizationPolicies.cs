namespace PiCommandCenter.Api;

/// <summary>Authorization policy names owned by the versioned API.</summary>
public static class ApiAuthorizationPolicies
{
    /// <summary>
    /// Native <c>/api/v1</c> callers: Identity bearer tokens only. Cookies are never consulted,
    /// so a browser session cannot be replayed against the native surface.
    /// </summary>
    public const string NativeApi = "NativeApi";
}
