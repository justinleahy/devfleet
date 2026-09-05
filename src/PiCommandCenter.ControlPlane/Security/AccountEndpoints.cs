using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using PiCommandCenter.Infrastructure.Security;

namespace PiCommandCenter.ControlPlane.Security;

/// <summary>
/// Cookie login and logout. Protected by antiforgery. Failures never echo the password.
/// </summary>
internal static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/account/login", LoginAsync)
            .AllowAnonymous();
        routes.MapPost("/account/logout", LogoutAsync)
            .AllowAnonymous();

        return routes;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        SignInManager<IdentityUser> signInManager,
        IOptions<AdminOptions> adminOptions)
    {
        var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
        var username = form["username"].ToString();
        var password = form["password"].ToString();
        var returnUrl = LocalizeReturnUrl(form["returnUrl"].ToString());

        var expectedUser = adminOptions.Value.Username;
        if (!string.Equals(username, expectedUser, StringComparison.Ordinal))
        {
            return Results.Redirect(InvalidLogin(returnUrl));
        }

        var result = await signInManager
            .PasswordSignInAsync(username, password, isPersistent: true, lockoutOnFailure: true)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return Results.Redirect(InvalidLogin(returnUrl));
        }

        return Results.Redirect(returnUrl);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        SignInManager<IdentityUser> signInManager)
    {
        var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
        await signInManager.SignOutAsync().ConfigureAwait(false);
        var returnUrl = LocalizeReturnUrl(form["returnUrl"].ToString(), fallback: "/login");
        return Results.Redirect(returnUrl);
    }

    private static string InvalidLogin(string returnUrl) =>
        "/login?error=invalid&returnUrl=" + Uri.EscapeDataString(returnUrl);

    private static string LocalizeReturnUrl(string? returnUrl, string fallback = "/")
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return fallback;
        }

        if (returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return returnUrl;
        }

        return fallback;
    }
}
