namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// Deterministic admin password and node token used only by test hosts.
/// Production setup always generates high-entropy secrets instead.
/// </summary>
public static class AuthTestMaterial
{
    public const string Username = "admin";

    public const string Password = "PiCommandCenter.Test.Admin.1";

    public static string NodeTokenHex { get; } = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData("pi-command-center-test-node"u8.ToArray()));

    public static (string PasswordFile, string CredentialFile) WriteTo(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var passwordFile = Path.Combine(directory, "admin.password.hash");
        var credentialFile = Path.Combine(directory, "node.token");
        PrivateFileAccess.WritePrivateFile(passwordFile, AuthMaterialLoader.HashPassword(Password));
        PrivateFileAccess.WritePrivateFile(credentialFile, NodeTokenHex);
        return (passwordFile, credentialFile);
    }
}
