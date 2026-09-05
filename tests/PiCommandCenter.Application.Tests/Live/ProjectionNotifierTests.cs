using PiCommandCenter.Application.Live;

namespace PiCommandCenter.Application.Tests.Live;

public class ProjectionNotifierTests
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid Request = Guid.NewGuid();

    [Fact]
    public void Every_live_subscriber_sees_a_published_change()
    {
        var notifier = new ProjectionNotifier();
        var first = new List<ProjectionChange>();
        var second = new List<ProjectionChange>();
        using var a = notifier.Subscribe(first.Add);
        using var b = notifier.Subscribe(second.Add);

        notifier.Publish(ProjectionChange.Request(Project, Request));

        Assert.Equal(Request, Assert.Single(first).RequestId);
        Assert.Equal(Request, Assert.Single(second).RequestId);
    }

    [Fact]
    public void A_disposed_subscription_stops_receiving_changes()
    {
        var notifier = new ProjectionNotifier();
        var seen = new List<ProjectionChange>();
        var subscription = notifier.Subscribe(seen.Add);

        notifier.Publish(ProjectionChange.Fleet());
        subscription.Dispose();
        notifier.Publish(ProjectionChange.Fleet());

        Assert.Single(seen);
    }

    [Fact]
    public void A_faulted_subscriber_never_breaks_the_write_that_published()
    {
        var notifier = new ProjectionNotifier();
        var delivered = 0;
        using var faulting = notifier.Subscribe(_ => throw new InvalidOperationException("view is gone"));
        using var healthy = notifier.Subscribe(_ => delivered++);

        notifier.Publish(ProjectionChange.Project(Project));

        Assert.Equal(1, delivered);
    }

    [Fact]
    public void Request_scope_reaches_that_request_and_its_project_only()
    {
        var change = ProjectionChange.Request(Project, Request);

        Assert.True(change.AffectsFleet);
        Assert.True(change.AffectsProject(Project));
        Assert.False(change.AffectsProject(Guid.NewGuid()));
        Assert.True(change.AffectsRequest(Request));
        Assert.False(change.AffectsRequest(Guid.NewGuid()));
    }

    [Fact]
    public void Fleet_scope_reaches_every_view()
    {
        var change = ProjectionChange.Fleet();

        Assert.True(change.AffectsFleet);
        Assert.True(change.AffectsProject(Project));
        Assert.True(change.AffectsRequest(Request));
    }
}
