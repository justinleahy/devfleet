namespace PiCommandCenter.Application.Live;

/// <summary>
/// Singleton fan-out of committed projection changes to live views. Subscribers are held in an
/// immutable snapshot swapped under a lock, so publishing never blocks on subscription churn and
/// a subscriber disposing during a publish cannot corrupt the iteration.
/// </summary>
public sealed class ProjectionNotifier : IProjectionNotifier
{
    private readonly Lock _gate = new();
    private Subscription[] _subscriptions = [];

    /// <inheritdoc />
    public void Publish(ProjectionChange change)
    {
        var snapshot = Volatile.Read(ref _subscriptions);
        foreach (var subscription in snapshot)
        {
            try
            {
                subscription.Handler(change);
            }
            catch (Exception)
            {
                // A faulted view must never break the durable write that published the change.
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<ProjectionChange> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(this, handler);
        lock (_gate)
        {
            var current = _subscriptions;
            var next = new Subscription[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = subscription;
            Volatile.Write(ref _subscriptions, next);
        }

        return subscription;
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            var current = _subscriptions;
            var index = Array.IndexOf(current, subscription);
            if (index < 0)
            {
                return;
            }

            if (current.Length == 1)
            {
                Volatile.Write(ref _subscriptions, []);
                return;
            }

            var next = new Subscription[current.Length - 1];
            Array.Copy(current, next, index);
            Array.Copy(current, index + 1, next, index, current.Length - index - 1);
            Volatile.Write(ref _subscriptions, next);
        }
    }

    private sealed class Subscription(ProjectionNotifier owner, Action<ProjectionChange> handler)
        : IDisposable
    {
        private int _disposed;

        internal Action<ProjectionChange> Handler { get; } = handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(this);
            }
        }
    }
}
