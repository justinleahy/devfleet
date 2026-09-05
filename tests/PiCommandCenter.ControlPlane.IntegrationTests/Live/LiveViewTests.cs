using PiCommandCenter.Application.Live;
using PiCommandCenter.Web.Components.Live;

namespace PiCommandCenter.ControlPlane.IntegrationTests.Live;

/// <summary>
/// Covers the refresh pump every live page depends on: a burst of committed writes must collapse
/// into one reload, changes outside the page's scope must not reload it, and disposal must stop it.
/// </summary>
public class LiveViewTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid Request = Guid.NewGuid();

    [Fact]
    public async Task A_burst_of_changes_causes_one_reload()
    {
        var notifier = new ProjectionNotifier();
        var reloads = 0;
        await using var view = new LiveView(
            notifier,
            change => change.AffectsRequest(Request),
            () =>
            {
                Interlocked.Increment(ref reloads);
                return Task.CompletedTask;
            });

        for (var i = 0; i < 8; i++)
        {
            notifier.Publish(ProjectionChange.Request(Project, Request));
        }

        await WaitForAsync(() => Volatile.Read(ref reloads) >= 1);
        await Task.Delay(LiveView.CoalesceWindow * 3);

        Assert.Equal(1, Volatile.Read(ref reloads));
    }

    [Fact]
    public async Task A_change_outside_the_view_scope_never_reloads_it()
    {
        var notifier = new ProjectionNotifier();
        var reloads = 0;
        await using var view = new LiveView(
            notifier,
            change => change.AffectsRequest(Request),
            () =>
            {
                Interlocked.Increment(ref reloads);
                return Task.CompletedTask;
            });

        notifier.Publish(ProjectionChange.Request(Project, Guid.NewGuid()));
        await Task.Delay(LiveView.CoalesceWindow * 4);

        Assert.Equal(0, Volatile.Read(ref reloads));
    }

    [Fact]
    public async Task A_change_published_after_disposal_is_ignored()
    {
        var notifier = new ProjectionNotifier();
        var reloads = 0;
        var view = new LiveView(
            notifier,
            _ => true,
            () =>
            {
                Interlocked.Increment(ref reloads);
                return Task.CompletedTask;
            });

        notifier.Publish(ProjectionChange.Fleet());
        await WaitForAsync(() => Volatile.Read(ref reloads) >= 1);
        await view.DisposeAsync();

        notifier.Publish(ProjectionChange.Fleet());
        await Task.Delay(LiveView.CoalesceWindow * 4);

        Assert.Equal(1, Volatile.Read(ref reloads));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("The live view never reloaded within the timeout.");
    }
}
