using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>进程内共享更新状态；帮助页可在自动检查结束后读取同一结果。</summary>
public sealed class UpdateStateStore : IUpdateStateStore
{
    private readonly object _sync = new();
    private readonly IUiDispatcher _dispatcher;
    private UpdateStateSnapshot _current = new(false, null, null);

    public UpdateStateStore() : this(InlineUiDispatcher.Instance)
    {
    }

    public UpdateStateStore(IUiDispatcher dispatcher) =>
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public UpdateStateSnapshot Current
    {
        get { lock (_sync) return _current; }
    }

    public event EventHandler<UpdateStateSnapshot>? Changed;

    public void Initialize(UpdateCheckResult? successfulResult)
    {
        UpdateStateSnapshot snapshot;
        lock (_sync)
        {
            _current = new(false, successfulResult, successfulResult);
            snapshot = _current;
        }
        Publish(snapshot);
    }

    public void BeginCheck()
    {
        UpdateStateSnapshot snapshot;
        lock (_sync)
        {
            _current = _current with { IsChecking = true };
            snapshot = _current;
        }
        Publish(snapshot);
    }

    public void Complete(UpdateCheckResult result)
    {
        UpdateStateSnapshot snapshot;
        lock (_sync)
        {
            _current = new(
                false,
                result,
                result.IsSuccessful ? result : _current.LastSuccessfulResult);
            snapshot = _current;
        }
        Publish(snapshot);
    }

    private void Publish(UpdateStateSnapshot snapshot)
    {
        void Raise()
        {
            var handlers = Changed;
            if (handlers is null) return;
            foreach (EventHandler<UpdateStateSnapshot> handler in handlers.GetInvocationList())
            {
                try { handler(this, snapshot); }
                catch { }
            }
        }
        if (_dispatcher.HasThreadAccess)
        {
            Raise();
            return;
        }

        try { _dispatcher.TryEnqueue(Raise); }
        catch { }
    }
}
