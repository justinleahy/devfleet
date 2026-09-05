using PiCommandCenter.Application.Live;

namespace PiCommandCenter.Web.Components.Live;

/// <summary>
/// Drives one page's refresh from committed projection changes instead of a fixed poll.
/// A page constructs it with the scope filter it cares about and its own reload delegate; the
/// pump collapses a burst of writes into a single reload and still reloads on a safety interval
/// so a missed or unpublished write can never leave a stale view on screen.
/// </summary>
/// <remarks>
/// The reload delegate is invoked on a background task, so pages marshal to the renderer with
/// <c>InvokeAsync</c> inside the delegate. Reloads never overlap: the pump awaits each one.
/// </remarks>
public sealed class LiveView : IAsyncDisposable
{
    /// <summary>Window a burst of writes is collapsed into, so a chatty node causes one reload.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>Reload cadence when nothing is published, covering any unpublished write.</summary>
    public static readonly TimeSpan SafetyInterval = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _pending = new(0, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<ProjectionChange, bool> _affects;
    private readonly Func<Task> _refreshAsync;
    private readonly IDisposable _subscription;
    private readonly Task _pump;

    public LiveView(
        IProjectionNotifier notifier,
        Func<ProjectionChange, bool> affects,
        Func<Task> refreshAsync)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(affects);
        ArgumentNullException.ThrowIfNull(refreshAsync);

        _affects = affects;
        _refreshAsync = refreshAsync;
        _subscription = notifier.Subscribe(Signal);
        _pump = PumpAsync(_cts.Token);
    }

    /// <summary>Publisher-thread callback: records interest and returns without doing any work.</summary>
    private void Signal(ProjectionChange change)
    {
        if (!_affects(change) || _cts.IsCancellationRequested)
        {
            return;
        }

        // Bounded at one: further writes inside the window are covered by the pending reload.
        if (_pending.CurrentCount == 0)
        {
            try
            {
                _pending.Release();
            }
            catch (SemaphoreFullException)
            {
                // Raced with another publisher; a reload is already pending.
            }
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var published = await _pending.WaitAsync(SafetyInterval, cancellationToken)
                    .ConfigureAwait(false);
                if (published)
                {
                    await Task.Delay(CoalesceWindow, cancellationToken).ConfigureAwait(false);

                    // Anything published during the window is satisfied by the reload below.
                    while (await _pending.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                    {
                    }
                }

                await _refreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Circuit torn down.
        }
        catch (ObjectDisposedException)
        {
            // Circuit torn down mid-wait.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _subscription.Dispose();
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
        _pending.Dispose();
    }
}
