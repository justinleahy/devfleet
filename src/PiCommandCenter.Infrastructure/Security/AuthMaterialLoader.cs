using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;

namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// Loads admin password hash and node token from private files. Runtime never invents them.
/// </summary>
public static class AuthMaterialLoader
{
    public static string MissingAdminFileMessage(string path) =>
        $"Admin password file is missing at '{path}'. Run the control plane with --setup (or scripts/setup-local.sh) to generate the administrator hash. Runtime does not invent defaults.";

    public static string MissingNodeFileMessage(string path) =>
        $"Node credential file is missing at '{path}'. Run the control plane with --setup (or scripts/setup-local.sh) to generate a 256-bit node token. Runtime does not invent defaults.";

    public static string LoadPasswordHash(AdminOptions admin, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(environment);

        if (string.IsNullOrWhiteSpace(admin.Username))
        {
            throw new InvalidOperationException("Admin:Username must be configured.");
        }

        if (string.IsNullOrWhiteSpace(admin.PasswordFile))
        {
            throw new InvalidOperationException(
                "Admin:PasswordFile must be configured. Run --setup to create the private hash file.");
        }

        var path = PrivateFileAccess.ExpandPath(admin.PasswordFile);
        return PrivateFileAccess.ReadPrivateFile(path, MissingAdminFileMessage(path));
    }

    public static NodeTokenCredential LoadNodeToken(NodeAuthenticationOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (string.IsNullOrWhiteSpace(options.CredentialFile))
        {
            throw new InvalidOperationException(
                "NodeAuthentication:CredentialFile must be configured. Run --setup to create the private token file.");
        }

        var path = PrivateFileAccess.ExpandPath(options.CredentialFile);
        var hex = PrivateFileAccess.ReadPrivateFile(path, MissingNodeFileMessage(path));
        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(hex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Node credential file is not valid hexadecimal.", ex);
        }

        return new NodeTokenCredential(bytes);
    }

    public static string GeneratePassword()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    public static string GenerateNodeTokenHex()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    public static string HashPassword(string password)
    {
        var hasher = new PasswordHasher<IdentityUser>();
        return hasher.HashPassword(new IdentityUser(), password);
    }
}
