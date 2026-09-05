using Microsoft.Extensions.Options;
using PiCommandCenter.Node;

namespace PiCommandCenter.Node.Tests;

public sealed class NodeCredentialLoaderTests
{
    [Fact]
    public void Load_reads_owner_only_hex_token()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pi-cc-node-cred", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "node.token");
        var hex = Convert.ToHexString(Enumerable.Repeat((byte)0xAB, 32).ToArray());
        File.WriteAllText(path, hex);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var loader = new NodeCredentialLoader(Options.Create(new NodeAuthenticationOptions
        {
            CredentialFile = path,
            Header = "Authorization",
            Scheme = "Bearer",
        }));

        var credential = loader.Load();
        Assert.Equal(hex, credential.TokenHex);
        Assert.Equal("Authorization", credential.Header);
        Assert.DoesNotContain(hex, credential.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_fails_when_file_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), "pi-cc-node-cred-missing", Guid.NewGuid().ToString("N"), "node.token");
        var loader = new NodeCredentialLoader(Options.Create(new NodeAuthenticationOptions
        {
            CredentialFile = path,
        }));
        var ex = Assert.Throws<InvalidOperationException>(() => loader.Load());
        Assert.Contains("--setup", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not invent a token", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_fails_when_permissions_are_too_open()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "pi-cc-node-cred", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "node.token");
        File.WriteAllText(path, Convert.ToHexString(new byte[32]));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var loader = new NodeCredentialLoader(Options.Create(new NodeAuthenticationOptions
        {
            CredentialFile = path,
        }));

        var ex = Assert.Throws<InvalidOperationException>(() => loader.Load());
        Assert.Contains("0600", ex.Message, StringComparison.Ordinal);
    }
}
