using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// Explicit first-run setup: writes one admin password hash and one 256-bit node token
/// to private files. Does not run as a silent default at host startup.
/// </summary>
public static class ControlPlaneAuthSetup
{
    public static bool IsSetupRequested(IEnumerable<string> args) =>
        args.Any(argument => string.Equals(argument, "--setup", StringComparison.OrdinalIgnoreCase));

    public static SetupResult Run(IConfiguration configuration, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var admin = new AdminOptions();
        configuration.GetSection(AdminOptions.SectionName).Bind(admin);
        var node = new NodeAuthenticationOptions();
        configuration.GetSection(NodeAuthenticationOptions.SectionName).Bind(node);

        if (string.IsNullOrWhiteSpace(admin.Username))
        {
            admin.Username = "admin";
        }

        var passwordPath = Path.GetFullPath(PrivateFileAccess.ExpandPath(admin.PasswordFile));
        var tokenPath = Path.GetFullPath(PrivateFileAccess.ExpandPath(node.CredentialFile));

        if (!force && (File.Exists(passwordPath) || File.Exists(tokenPath)))
        {
            throw new InvalidOperationException(
                $"Auth material already exists at '{passwordPath}' or '{tokenPath}'. Re-run with --force to overwrite.");
        }

        var password = AuthMaterialLoader.GeneratePassword();
        var hash = AuthMaterialLoader.HashPassword(password);
        var tokenHex = AuthMaterialLoader.GenerateNodeTokenHex();

        PrivateFileAccess.WritePrivateFile(passwordPath, hash);
        PrivateFileAccess.WritePrivateFile(tokenPath, tokenHex);

        return new SetupResult(admin.Username, password, passwordPath, tokenPath, tokenHex);
    }

    public sealed record SetupResult(
        string Username,
        string OneTimePassword,
        string PasswordFile,
        string CredentialFile,
        string NodeTokenHex);
}
