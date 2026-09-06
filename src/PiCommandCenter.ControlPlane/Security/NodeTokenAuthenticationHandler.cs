using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PiCommandCenter.Infrastructure.Security;

namespace PiCommandCenter.ControlPlane.Security;

/// <summary>
/// Authenticates node clients using the credential registry.
/// The presented secret is never written to logs or the authentication ticket.
/// </summary>
public sealed class NodeTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    NodeCredentialRegistry credentials,
    IOptions<NodeAuthenticationOptions> nodeOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = nodeOptions.Value;
        var headerName = string.IsNullOrWhiteSpace(configured.Header)
            ? NodeAuthenticationOptions.DefaultHeader
            : configured.Header;
        var scheme = string.IsNullOrWhiteSpace(configured.Scheme)
            ? NodeAuthenticationOptions.DefaultScheme
            : configured.Scheme;

        if (!Request.Headers.TryGetValue(headerName, out var headerValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var raw = headerValues.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string presented;
        if (!string.IsNullOrEmpty(scheme)
            && raw.StartsWith(scheme + " ", StringComparison.OrdinalIgnoreCase))
        {
            presented = raw[(scheme.Length + 1)..].Trim();
        }
        else if (string.Equals(headerName, "Authorization", StringComparison.OrdinalIgnoreCase)
                 && raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            presented = raw["Bearer ".Length..].Trim();
        }
        else
        {
            presented = raw.Trim();
        }

        if (presented.Length != NodeCredentialRegistry.CredentialHexLength)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid node credential."));
        }

        byte[] candidate;
        try
        {
            candidate = Convert.FromHexString(presented);
        }
        catch (FormatException)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid node credential."));
        }

        Guid nodeId;
        bool authenticated;
        try
        {
            authenticated = credentials.TryResolve(candidate, out nodeId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }

        if (!authenticated)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid node credential."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, nodeId.ToString("D")), new Claim(ClaimTypes.Role, "Node")],
            NodeTokenDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), NodeTokenDefaults.AuthenticationScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
