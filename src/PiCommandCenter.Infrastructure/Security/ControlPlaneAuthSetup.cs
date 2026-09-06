using Microsoft.Extensions.Configuration;

namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// Explicit first-run setup for the administrator password hash.
/// Node identity and credential provisioning is handled by the local setup script.
/// </summary>
public static class ControlPlaneAuthSetup
{
    public static bool IsSetupRequested(IEnumerable<string> args) =>
        args.Any(argument => string.Equals(argument, "--setup", StringComparison.OrdinalIgnoreCase));

    public static SetupResult Run(
        IConfiguration configuration,
        bool force = false,
        string? adminPassword = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var admin = new AdminOptions();
        configuration.GetSection(AdminOptions.SectionName).Bind(admin);

        if (string.IsNullOrWhiteSpace(admin.Username))
        {
            admin.Username = "admin";
        }

        var passwordPath = Path.GetFullPath(PrivateFileAccess.ExpandPath(admin.PasswordFile));

        if (!force && File.Exists(passwordPath))
        {
            throw new InvalidOperationException(
                $"Administrator auth material already exists at '{passwordPath}'. Re-run with --force to overwrite.");
        }

        var password = adminPassword ?? AuthMaterialLoader.GeneratePassword();
        if (password.Length < 12)
        {
            throw new InvalidOperationException("Administrator password must be at least 12 characters.");
        }

        var hash = AuthMaterialLoader.HashPassword(password);

        PrivateFileAccess.WritePrivateFile(passwordPath, hash);

        return new SetupResult(admin.Username, password, passwordPath);
    }

    public sealed record SetupResult(
        string Username,
        string OneTimePassword,
        string PasswordFile);
}
