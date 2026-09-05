using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PiCommandCenter.Infrastructure.Security;

namespace PiCommandCenter.Api;

/// <summary>
/// Native bearer authentication for <c>/api/v1</c>: JSON login and refresh for the single local
/// administrator. Every failure is the same generic 401 so callers cannot distinguish an unknown
/// user, a wrong password, a locked account, or a stale refresh token. Nothing here touches cookies,
/// and there is deliberately no registration, password management, or logout: opaque tokens simply
/// expire (access 1h, refresh 14d) or die with the user's security stamp.
/// </summary>
internal static class NativeAuthEndpoints
{
    public static void MapNativeAuthEndpoints(this RouteGroupBuilder group)
    {
        var auth = group.MapGroup("/auth").WithTags("Auth").AllowAnonymous();
        auth.MapPost("/login", LoginAsync)
            .Produces<AccessTokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        auth.MapPost("/refresh", RefreshAsync)
            .Produces<AccessTokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
    }

    /// <summary>
    /// On success the bearer scheme's sign-in writes the <see cref="AccessTokenResponse"/> body itself,
    /// so the handler returns an empty result rather than a second payload.
    /// </summary>
    private static async Task<Results<EmptyHttpResult, ProblemHttpResult>> LoginAsync(
        [FromBody] LoginRequest request,
        SignInManager<IdentityUser> signInManager,
        IOptions<AdminOptions> adminOptions)
    {
        if (string.IsNullOrEmpty(request.Password)
            || !string.Equals(request.Username, adminOptions.Value.Username, StringComparison.Ordinal))
        {
            return Unauthorized();
        }

        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        var result = await signInManager
            .PasswordSignInAsync(request.Username, request.Password, isPersistent: false, lockoutOnFailure: true);

        return result.Succeeded ? TypedResults.Empty : Unauthorized();
    }

    private static async Task<Results<SignInHttpResult, ProblemHttpResult>> RefreshAsync(
        [FromBody] RefreshRequest request,
        SignInManager<IdentityUser> signInManager,
        IOptionsMonitor<BearerTokenOptions> bearerOptions,
        TimeProvider timeProvider)
    {
        var refreshTokenProtector = bearerOptions.Get(IdentityConstants.BearerScheme).RefreshTokenProtector;
        var refreshTicket = refreshTokenProtector.Unprotect(request.RefreshToken);

        if (refreshTicket?.Properties.ExpiresUtc is not { } expiresUtc
            || timeProvider.GetUtcNow() >= expiresUtc
            || await signInManager.ValidateSecurityStampAsync(refreshTicket.Principal) is not { } user)
        {
            return Unauthorized();
        }

        var principal = await signInManager.CreateUserPrincipalAsync(user);
        return TypedResults.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
    }

    private static ProblemHttpResult Unauthorized() =>
        TypedResults.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
}

/// <summary>Body of <c>POST /api/v1/auth/login</c>.</summary>
internal sealed record LoginRequest(string Username, string Password);

/// <summary>Body of <c>POST /api/v1/auth/refresh</c>.</summary>
internal sealed record RefreshRequest(string RefreshToken);
