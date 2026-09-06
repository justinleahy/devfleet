namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// Deterministic authentication material used only by test hosts.
/// Production setup always generates high-entropy secrets instead.
/// </summary>
public static class AuthTestMaterial
{
    public const string Username = "admin";

    public const string Password = "PiCommandCenter.Test.Admin.1";
    private static readonly Guid TestNodeId = new("16a9c414-dc4b-482a-91be-4f3b947894ff");

    public static string NodeTokenHex { get; } = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData("pi-command-center-test-node"u8));

    public static AuthTestMaterialResult WriteTo(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        PrivateFileAccess.CreatePrivateDirectory(directory);

        var passwordFile = Path.Combine(directory, "admin.password.hash");
        var credentialDirectory = Path.Combine(directory, "node-credentials");
        PrivateFileAccess.CreatePrivateDirectory(credentialDirectory);

        var credentialFile = Path.Combine(credentialDirectory, $"{TestNodeId:D}.token");
        PrivateFileAccess.WritePrivateFile(passwordFile, AuthMaterialLoader.HashPassword(Password));
        PrivateFileAccess.WritePrivateFile(credentialFile, NodeTokenHex);

        return new AuthTestMaterialResult(
            passwordFile,
            credentialDirectory,
            TestNodeId,
            NodeTokenHex);
    }
}

public sealed class AuthTestMaterialResult
{
    internal AuthTestMaterialResult(
        string passwordFile,
        string credentialDirectory,
        Guid authenticatedNodeId,
        string nodeTokenHex)
    {
        PasswordFile = passwordFile;
        CredentialDirectory = credentialDirectory;
        AuthenticatedNodeId = authenticatedNodeId;
        NodeTokenHex = nodeTokenHex;
    }

    public string PasswordFile { get; }

    public string CredentialDirectory { get; }

    public Guid AuthenticatedNodeId { get; }

    public string NodeTokenHex { get; }
}
