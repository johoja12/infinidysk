namespace NzbWebDAV.Services.NativeCache;

/// <summary>
/// Per-device writer gate. Block commits enter shared, so blocks of one file (or of several
/// files on one device) commit in parallel. Placement, scans, checkpoints, eviction and
/// relocation enter exclusive and wait for in-flight commits to drain. Waiters are served in
/// arrival order and a queued exclusive waiter blocks new shared entrants, so a steady commit
/// stream cannot starve maintenance.
/// </summary>
internal sealed class NativeCacheDeviceGate
{
    private readonly Lock _sync = new();
    private readonly LinkedList<Waiter> _waiters = [];
    private int _shared;
    private bool _exclusive;

    /// <summary>No commit or maintenance holds or awaits the gate.</summary>
    public bool IsIdle { get { lock (_sync) return !_exclusive && _shared == 0 && _waiters.Count == 0; } }

    /// <summary>Enters exclusive mode, waiting for current holders.</summary>
    public Task WaitAsync(CancellationToken cancellationToken = default) => EnterAsync(exclusive: true, cancellationToken);

    /// <summary>Enters exclusive mode; with a zero timeout, only when the gate is idle.</summary>
    public async Task<bool> WaitAsync(int millisecondsTimeout, CancellationToken cancellationToken)
    {
        if (millisecondsTimeout != 0)
            throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout), "Only immediate or unbounded waits are supported.");
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_exclusive || _shared != 0 || _waiters.Count != 0) return false;
            _exclusive = true;
            return true;
        }
    }

    /// <summary>Leaves exclusive mode.</summary>
    public void Release()
    {
        lock (_sync)
        {
            if (!_exclusive) throw new SemaphoreFullException();
            _exclusive = false;
            GrantWaiters();
        }
    }

    public Task EnterSharedAsync(CancellationToken cancellationToken) => EnterAsync(exclusive: false, cancellationToken);

    /// <summary>Enters shared mode unless an exclusive holder or waiter is present.</summary>
    public bool TryEnterShared()
    {
        lock (_sync)
        {
            if (_exclusive || _waiters.Count != 0) return false;
            _shared++;
            return true;
        }
    }

    public void ReleaseShared()
    {
        lock (_sync)
        {
            if (_shared == 0) throw new SemaphoreFullException();
            _shared--;
            GrantWaiters();
        }
    }

    private Task EnterAsync(bool exclusive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_sync)
        {
            if (_waiters.Count == 0 && !_exclusive && (!exclusive || _shared == 0))
            {
                if (exclusive) _exclusive = true;
                else _shared++;
                return Task.CompletedTask;
            }
            waiter = new Waiter(exclusive);
            waiter.Node = _waiters.AddLast(waiter);
        }
        return cancellationToken.CanBeCanceled ? WaitGrantedAsync(waiter, cancellationToken) : waiter.Completion.Task;
    }

    private async Task WaitGrantedAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        // Disposed outside _sync: disposal waits for a running callback, which takes _sync.
        using var registration = cancellationToken.Register(static state =>
        {
            var (gate, pending, token) = ((NativeCacheDeviceGate, Waiter, CancellationToken))state!;
            gate.Cancel(pending, token);
        }, (this, waiter, cancellationToken));
        await waiter.Completion.Task.ConfigureAwait(false);
    }

    private void Cancel(Waiter waiter, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            // A waiter already granted owns the gate; its caller releases it as usual.
            if (waiter.Node?.List is null) return;
            _waiters.Remove(waiter.Node);
            waiter.Node = null;
            // Removing a queued exclusive waiter can unblock shared waiters behind it.
            GrantWaiters();
        }
        waiter.Completion.TrySetCanceled(cancellationToken);
    }

    // Called under _sync. Grants the head exclusive waiter, or every consecutive shared waiter.
    private void GrantWaiters()
    {
        while (_waiters.First is { } node && !_exclusive)
        {
            var waiter = node.Value;
            if (waiter.Exclusive && _shared != 0) return;
            _waiters.RemoveFirst();
            waiter.Node = null;
            if (waiter.Exclusive) _exclusive = true;
            else _shared++;
            waiter.Completion.TrySetResult();
            if (waiter.Exclusive) return;
        }
    }

    private sealed class Waiter(bool exclusive)
    {
        public bool Exclusive { get; } = exclusive;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
    }
}
