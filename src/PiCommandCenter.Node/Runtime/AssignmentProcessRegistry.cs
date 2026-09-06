using System.Runtime.CompilerServices;

namespace PiCommandCenter.Node.Runtime;

/// <summary>
/// Applies cooperative and termination budgets when an isolation handle cannot
/// accept those values on <see cref="IAssignmentProcessIsolation"/>.
/// </summary>
internal interface IAssignmentProcessStopInvoker
{
    Task<AssignmentProcessStopResult> StopAsync(
        IAssignmentProcessIsolation isolation,
        TimeSpan cooperativeBudget,
        TimeSpan terminationBudget,
        CancellationToken cancellationToken);
}

/// <summary>
/// Request-scoped registry of <see cref="IAssignmentProcessIsolation"/> handles.
/// Registrations never mix requests. Stop proof is aggregated from handles; an
/// empty set is not treated as known-zero.
/// </summary>
public sealed class AssignmentProcessRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RequestScope> _scopes = [];
    private readonly IsolationIdentityComparer _comparer = new();
    private readonly IAssignmentProcessStopInvoker _stopInvoker;

    public AssignmentProcessRegistry()
        : this(new BudgetedAssignmentProcessStopInvoker())
    {
    }

    internal AssignmentProcessRegistry(IAssignmentProcessStopInvoker stopInvoker)
    {
        _stopInvoker = stopInvoker ?? throw new ArgumentNullException(nameof(stopInvoker));
    }

    public IDisposable Register(Guid requestId, IAssignmentProcessIsolation isolation)
    {
        ArgumentNullException.ThrowIfNull(isolation);
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("Request id is required.", nameof(requestId));
        }

        lock (_gate)
        {
            if (TryFindOwner(isolation, out var owner) && owner != requestId)
            {
                throw new InvalidOperationException(
                    "Isolation handle is already registered to another request.");
            }

            var scope = GetOrCreate(requestId);
            if (scope.Contains(isolation, _comparer))
            {
                throw new InvalidOperationException(
                    "Isolation handle identity is already registered for this request.");
            }

            var registration = new Registration(this, requestId, isolation);
            scope.Add(registration);
            return registration;
        }
    }

    public IReadOnlyList<AssignmentProcessIdentity> Snapshot(Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("Request id is required.", nameof(requestId));
        }

        lock (_gate)
        {
            if (!_scopes.TryGetValue(requestId, out var scope))
            {
                return [];
            }

            return scope.SnapshotIdentities();
        }
    }

    public async Task<AssignmentProcessStopResult> StopAsync(
        Guid requestId,
        TimeSpan cooperativeBudget,
        TimeSpan terminationBudget,
        CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("Request id is required.", nameof(requestId));
        }

        IAssignmentProcessIsolation[] handles;
        lock (_gate)
        {
            if (!_scopes.TryGetValue(requestId, out var scope) || scope.Count == 0)
            {
                return AssignmentProcessStopResult.Unproven();
            }

            handles = scope.Handles();
        }

        var discovered = new List<AssignmentProcessIdentity>();
        var seen = new HashSet<(int Pid, long Start)>();
        var proven = true;

        foreach (var handle in handles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _stopInvoker
                .StopAsync(handle, cooperativeBudget, terminationBudget, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Proven)
            {
                proven = false;
            }

            foreach (var identity in result.DiscoveredProcesses)
            {
                if (seen.Add((identity.ProcessId, identity.StartTimeTicks)))
                {
                    discovered.Add(identity);
                }
            }

            if (handle.Identity is { } registered
                && seen.Add((registered.ProcessId, registered.StartTimeTicks)))
            {
                discovered.Add(registered);
            }
        }

        if (!proven)
        {
            return AssignmentProcessStopResult.Unproven(discovered);
        }

        return AssignmentProcessStopResult.Stopped(discovered);
    }

    private RequestScope GetOrCreate(Guid requestId)
    {
        if (_scopes.TryGetValue(requestId, out var existing))
        {
            return existing;
        }

        var created = new RequestScope();
        _scopes.Add(requestId, created);
        return created;
    }

    private bool TryFindOwner(IAssignmentProcessIsolation isolation, out Guid requestId)
    {
        foreach (var pair in _scopes)
        {
            if (pair.Value.Contains(isolation, _comparer))
            {
                requestId = pair.Key;
                return true;
            }
        }

        requestId = Guid.Empty;
        return false;
    }

    private void Unregister(Guid requestId, IAssignmentProcessIsolation isolation)
    {
        lock (_gate)
        {
            if (!_scopes.TryGetValue(requestId, out var scope))
            {
                return;
            }

            scope.Remove(isolation, _comparer);
            if (scope.Count == 0)
            {
                _scopes.Remove(requestId);
            }
        }
    }

    private sealed class RequestScope
    {
        private readonly List<Registration> _registrations = [];

        public int Count => _registrations.Count;

        public bool Contains(
            IAssignmentProcessIsolation isolation,
            IsolationIdentityComparer comparer)
        {
            foreach (var registration in _registrations)
            {
                if (comparer.Equals(registration.Isolation, isolation))
                {
                    return true;
                }
            }

            return false;
        }

        public void Add(Registration registration) => _registrations.Add(registration);

        public void Remove(
            IAssignmentProcessIsolation isolation,
            IsolationIdentityComparer comparer)
        {
            for (var i = _registrations.Count - 1; i >= 0; i--)
            {
                if (comparer.Equals(_registrations[i].Isolation, isolation))
                {
                    _registrations.RemoveAt(i);
                }
            }
        }

        public IAssignmentProcessIsolation[] Handles()
        {
            var handles = new IAssignmentProcessIsolation[_registrations.Count];
            for (var i = 0; i < _registrations.Count; i++)
            {
                handles[i] = _registrations[i].Isolation;
            }

            return handles;
        }

        public IReadOnlyList<AssignmentProcessIdentity> SnapshotIdentities()
        {
            var identities = new List<AssignmentProcessIdentity>(_registrations.Count);
            foreach (var registration in _registrations)
            {
                if (registration.Isolation.Identity is { } identity)
                {
                    identities.Add(identity);
                }
            }

            return identities.ToArray();
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly AssignmentProcessRegistry _registry;
        private readonly Guid _requestId;
        private int _disposed;

        public Registration(
            AssignmentProcessRegistry registry,
            Guid requestId,
            IAssignmentProcessIsolation isolation)
        {
            _registry = registry;
            _requestId = requestId;
            Isolation = isolation;
        }

        public IAssignmentProcessIsolation Isolation { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _registry.Unregister(_requestId, Isolation);
        }
    }

    private sealed class IsolationIdentityComparer : IEqualityComparer<IAssignmentProcessIsolation>
    {
        public bool Equals(IAssignmentProcessIsolation? x, IAssignmentProcessIsolation? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            if (x.Identity is { } left && y.Identity is { } right)
            {
                return left.ProcessId == right.ProcessId
                    && left.StartTimeTicks == right.StartTimeTicks;
            }

            return false;
        }

        public int GetHashCode(IAssignmentProcessIsolation obj)
        {
            if (obj.Identity is { } identity)
            {
                return HashCode.Combine(identity.ProcessId, identity.StartTimeTicks);
            }

            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}

/// <summary>
/// Default seam: isolation APIs take only a token, so budgets become a linked
/// cancellation deadline covering cooperative plus termination windows.
/// </summary>
internal sealed class BudgetedAssignmentProcessStopInvoker : IAssignmentProcessStopInvoker
{
    public async Task<AssignmentProcessStopResult> StopAsync(
        IAssignmentProcessIsolation isolation,
        TimeSpan cooperativeBudget,
        TimeSpan terminationBudget,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var total = SafeAdd(cooperativeBudget, terminationBudget);
        if (total > TimeSpan.Zero)
        {
            linked.CancelAfter(total);
        }

        try
        {
            return await isolation.StopIsolatedAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return isolation.Identity is { } identity
                ? AssignmentProcessStopResult.Unproven([identity])
                : AssignmentProcessStopResult.Unproven();
        }
    }

    private static TimeSpan SafeAdd(TimeSpan cooperative, TimeSpan termination)
    {
        cooperative = cooperative < TimeSpan.Zero ? TimeSpan.Zero : cooperative;
        termination = termination < TimeSpan.Zero ? TimeSpan.Zero : termination;
        var ticks = cooperative.Ticks + termination.Ticks;
        if (ticks <= 0)
        {
            return TimeSpan.Zero;
        }

        if (ticks >= TimeSpan.MaxValue.Ticks)
        {
            return TimeSpan.MaxValue;
        }

        return TimeSpan.FromTicks(ticks);
    }
}
