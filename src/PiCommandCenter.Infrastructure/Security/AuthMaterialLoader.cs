using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;

namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// Loads the admin password hash from a private file. Runtime never invents it.
/// </summary>
public static class AuthMaterialLoader
{
    public static string MissingAdminFileMessage(string path) =>
        $"Admin password file is missing at '{path}'. Run the control plane with --setup (or scripts/setup-local.sh) to generate the administrator hash. Runtime does not invent defaults.";

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

    public static string GeneratePassword()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    public static string HashPassword(string password)
    {
        var hasher = new PasswordHasher<IdentityUser>();
        return hasher.HashPassword(new IdentityUser(), password);
    }
}
