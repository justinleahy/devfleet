using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;

namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// Immutable startup registry of node identities and their private credentials.
/// </summary>
public sealed class NodeCredentialRegistry
{
    public const int CredentialHexLength = 64;
    public const int MaxCredentialFileBytes = CredentialHexLength;

    public const int MaxCredentialFiles = 1024;

    private const int CredentialByteLength = CredentialHexLength / 2;
    private const string CredentialFileSuffix = ".token";
    private const UnixFileMode SharedUnixPermissions =
        UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherWrite
        | UnixFileMode.OtherExecute;

    private readonly Credential[] _credentials;

    public NodeCredentialRegistry(NodeAuthenticationOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        _credentials = Load(options.CredentialDirectory, environment.IsEnvironment("Testing"));
    }

    public bool TryResolve(ReadOnlySpan<byte> candidate, out Guid nodeId)
    {
        nodeId = default;
        if (candidate.Length != CredentialByteLength)
        {
            return false;
        }

        var matchCount = 0;
        foreach (var credential in _credentials)
        {
            if (!CryptographicOperations.FixedTimeEquals(credential.Token, candidate))
            {
                continue;
            }

            nodeId = credential.NodeId;
            matchCount++;
        }

        if (matchCount == 1)
        {
            return true;
        }

        nodeId = default;
        return false;
    }

    private static Credential[] Load(string configuredDirectory, bool allowMissing)
    {
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return allowMissing
                ? []
                : throw new InvalidOperationException("NodeAuthentication:CredentialDirectory must be configured.");
        }

        var directory = Path.GetFullPath(PrivateFileAccess.ExpandPath(configuredDirectory));
        if (!Directory.Exists(directory))
        {
            return allowMissing
                ? []
                : throw new InvalidOperationException(
                    $"Node credential directory is missing at '{directory}'. Provision per-node credentials with scripts/setup-local.sh.");
        }

        EnsureOwnerOnlyDirectory(directory);

        var paths = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (paths.Count == MaxCredentialFiles)
            {
                throw new InvalidOperationException(
                    $"Node credential directory exceeds the limit of {MaxCredentialFiles} entries.");
            }

            paths.Add(path);
        }

        if (paths.Count == 0)
        {
            return allowMissing
                ? []
                : throw new InvalidOperationException(
                    $"Node credential directory '{directory}' contains no credentials.");
        }

        paths.Sort(StringComparer.Ordinal);
        var credentials = new List<Credential>(paths.Count);
        try
        {
            foreach (var path in paths)
            {
                var nodeId = ParseNodeId(path);
                var token = ReadToken(path);
                if (ContainsToken(credentials, token))
                {
                    CryptographicOperations.ZeroMemory(token);
                    throw new InvalidOperationException(
                        $"Node credential directory '{directory}' contains a duplicate credential.");
                }

                credentials.Add(new Credential(nodeId, token));
            }

            return [.. credentials];
        }
        catch
        {
            foreach (var credential in credentials)
            {
                CryptographicOperations.ZeroMemory(credential.Token);
            }

            throw;
        }
    }

    private static Guid ParseNodeId(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw InvalidCredentialFile(path);
        }

        var fileName = Path.GetFileName(path);
        if (!fileName.EndsWith(CredentialFileSuffix, StringComparison.Ordinal))
        {
            throw InvalidCredentialFile(path);
        }

        var idText = fileName[..^CredentialFileSuffix.Length];
        if (!Guid.TryParseExact(idText, "D", out var nodeId)
            || !string.Equals(idText, nodeId.ToString("D"), StringComparison.Ordinal))
        {
            throw InvalidCredentialFile(path);
        }

        return nodeId;
    }

    private static byte[] ReadToken(string path)
    {
        PrivateFileAccess.EnsureOwnerOnlyFile(path);
        if (new FileInfo(path).Length != MaxCredentialFileBytes)
        {
            throw InvalidCredentialFile(path);
        }

        Span<byte> encoded = stackalloc byte[MaxCredentialFileBytes + 1];
        var bytesRead = 0;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   CredentialHexLength + 1,
                   FileOptions.SequentialScan))
        {
            while (bytesRead < encoded.Length)
            {
                var read = stream.Read(encoded[bytesRead..]);
                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
            }
        }

        if (bytesRead != CredentialHexLength)
        {
            throw InvalidCredentialFile(path);
        }

        var token = new byte[CredentialByteLength];
        for (var index = 0; index < token.Length; index++)
        {
            var high = ParseHexNibble(encoded[index * 2]);
            var low = ParseHexNibble(encoded[(index * 2) + 1]);
            if (high < 0 || low < 0)
            {
                CryptographicOperations.ZeroMemory(token);
                throw InvalidCredentialFile(path);
            }

            token[index] = (byte)((high << 4) | low);
        }

        return token;
    }

    private static bool ContainsToken(List<Credential> credentials, ReadOnlySpan<byte> candidate)
    {
        var duplicate = false;
        foreach (var credential in credentials)
        {
            duplicate |= CryptographicOperations.FixedTimeEquals(credential.Token, candidate);
        }

        return duplicate;
    }

    private static int ParseHexNibble(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
        _ => -1,
    };

    private static void EnsureOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || (File.GetUnixFileMode(path) & SharedUnixPermissions) != 0)
        {
            throw new InvalidOperationException(
                $"Node credential directory '{path}' must not grant access to group or other users.");
        }
    }

    private static InvalidOperationException InvalidCredentialFile(string path) =>
        new($"Node credential file '{path}' must be an owner-only regular file named as a lowercase node GUID with a .token suffix and contain exactly 64 hexadecimal characters.");

    private readonly record struct Credential(Guid NodeId, byte[] Token);
}
