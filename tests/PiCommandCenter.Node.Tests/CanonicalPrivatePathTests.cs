using PiCommandCenter.Node.Runtime.Claude.Hooks;
using PiCommandCenter.Node.Security;

namespace PiCommandCenter.Node.Tests;

public sealed class CanonicalPrivatePathTests : IDisposable
{
    private readonly string _repo = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pcc-canon-repo", Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _data = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pcc-canon-data", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repo, recursive: true);
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Host_owned_hook_directory_is_outside_the_repository()
    {
        var evaluator = new ClaudeReservationHookEvaluator(new FakeReservationGateway(), new ClaudeHookAuditLog());
        using var server = new ClaudeReservationHookServer(evaluator);
        var installer = new ClaudeHookSettingsInstaller(server, _data);
        var install = installer.Install(
            allowWrite: false,
            new ClaudeHookSessionContext("sess-1", Guid.NewGuid(), 1, _repo));

        Assert.True(CanonicalPrivatePath.IsOutsideRepository(install.RootDirectory, _repo));
        Assert.True(CanonicalPrivatePath.IsOutsideRepository(install.SettingsPath, _repo));
        Assert.True(CanonicalPrivatePath.IsOutsideRepository(install.HookPath, _repo));
        CanonicalPrivatePath.EnsureOutsideRepository(install.RootDirectory, _repo);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            Assert.True(CanonicalPrivatePath.IsOwnerPrivateDirectory(install.RootDirectory));
        }
    }

    [Fact]
    public void Path_inside_repo_is_rejected()
    {
        var inside = Path.Combine(_repo, "hooks");
        Assert.False(CanonicalPrivatePath.IsOutsideRepository(inside, _repo));
        Assert.Throws<InvalidOperationException>(
            () => CanonicalPrivatePath.EnsureOutsideRepository(inside, _repo));
    }
}
