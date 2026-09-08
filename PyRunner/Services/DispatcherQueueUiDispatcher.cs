using Microsoft.UI.Dispatching;

namespace PyRunner.Services;

/// <summary>将服务状态通知送回创建应用的 WinUI DispatcherQueue。</summary>
public sealed class DispatcherQueueUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue;

    public DispatcherQueueUiDispatcher(DispatcherQueue dispatcherQueue) =>
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));

    public bool HasThreadAccess => _dispatcherQueue.HasThreadAccess;

    public bool TryEnqueue(Action action) =>
        _dispatcherQueue.TryEnqueue(() => action());
}
