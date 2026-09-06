using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using PiCommandCenter.Infrastructure.Security;

namespace PiCommandCenter.Infrastructure.Tests;

public sealed class AuthMaterialTests
{
    [Fact]
    public void Setup_writes_an_owner_only_admin_password_hash()
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-cc-auth-setup", Guid.NewGuid().ToString("N"));
        var passwordFile = Path.Combine(root, "admin.password.hash");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:Username"] = "admin",
                ["Admin:PasswordFile"] = passwordFile,
            })
            .Build();

        var result = ControlPlaneAuthSetup.Run(configuration);
        var passwordHash = File.ReadAllText(passwordFile);
        var verification = new PasswordHasher<IdentityUser>().VerifyHashedPassword(
            new IdentityUser(),
            passwordHash,
            result.OneTimePassword);

        Assert.Equal("admin", result.Username);
        Assert.False(string.IsNullOrWhiteSpace(result.OneTimePassword));
        Assert.Equal(passwordFile, result.PasswordFile);
        Assert.NotEqual(PasswordVerificationResult.Failed, verification);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(passwordFile));
        }

        Assert.Throws<InvalidOperationException>(() => ControlPlaneAuthSetup.Run(configuration));
    }

    [Fact]
    public void Setup_hashes_a_supplied_admin_password()
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-cc-auth-setup", Guid.NewGuid().ToString("N"));
        var passwordFile = Path.Combine(root, "admin.password.hash");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:PasswordFile"] = passwordFile,
            })
            .Build();
        const string password = "SmallerPass!";

        var result = ControlPlaneAuthSetup.Run(configuration, adminPassword: password);
        var verification = new PasswordHasher<IdentityUser>().VerifyHashedPassword(
            new IdentityUser(),
            File.ReadAllText(passwordFile),
            password);

        Assert.Equal(password, result.OneTimePassword);
        Assert.NotEqual(PasswordVerificationResult.Failed, verification);
    }

    [Fact]
    public void Setup_rejects_a_supplied_admin_password_shorter_than_twelve_characters()
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-cc-auth-setup", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:PasswordFile"] = Path.Combine(root, "admin.password.hash"),
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => ControlPlaneAuthSetup.Run(configuration, adminPassword: "too-short"));

        Assert.Contains("at least 12 characters", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_password_file_message_names_setup()
    {
        var message = AuthMaterialLoader.MissingAdminFileMessage("/tmp/missing.hash");
        Assert.Contains("--setup", message, StringComparison.Ordinal);
        Assert.Contains("does not invent defaults", message, StringComparison.Ordinal);
    }
}
