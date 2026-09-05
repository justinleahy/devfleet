using PiCommandCenter.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Identity;

namespace PiCommandCenter.Infrastructure.Tests;

public sealed class AuthMaterialTests
{
    [Fact]
    public void Setup_writes_owner_only_hash_and_token_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "pi-cc-auth-setup", Guid.NewGuid().ToString("N"));
        var passwordFile = Path.Combine(root, "admin.password.hash");
        var tokenFile = Path.Combine(root, "node.token");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:Username"] = "admin",
                ["Admin:PasswordFile"] = passwordFile,
                ["NodeAuthentication:CredentialFile"] = tokenFile,
            })
            .Build();

        var result = ControlPlaneAuthSetup.Run(configuration);

        Assert.Equal("admin", result.Username);
        Assert.False(string.IsNullOrWhiteSpace(result.OneTimePassword));
        Assert.True(File.Exists(passwordFile));
        Assert.True(File.Exists(tokenFile));
        Assert.Equal(64, File.ReadAllText(tokenFile).Trim().Length);
        Assert.DoesNotContain(result.OneTimePassword, File.ReadAllText(passwordFile), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(passwordFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tokenFile));
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
                ["NodeAuthentication:CredentialFile"] = Path.Combine(root, "node.token"),
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
                ["NodeAuthentication:CredentialFile"] = Path.Combine(root, "node.token"),
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
