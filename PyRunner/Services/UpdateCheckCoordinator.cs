using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>
/// 协调自动/手动检查、24小时节流和共享在途任务。网络失败只更新最后尝试状态，
/// 不会清除持久化或进程内的最后一次成功结果。
/// </summary>
public sealed class UpdateCheckCoordinator : IDisposable
{
    private static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);

    private readonly object _sync = new();
    private readonly IUpdateService _updates;
    private readonly IUpdateCacheStore _cache;
    private readonly IUpdateStateStore _state;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private Task<UpdateCheckResult>? _inFlight;
    private Task _worker = Task.CompletedTask;
    private bool _disposed;
    private bool _lifetimeDisposed;

    public UpdateCheckCoordinator(
        IUpdateService updates,
        IUpdateCacheStore cache,
        IUpdateStateStore state,
        TimeProvider timeProvider)
    {
        _updates = updates;
        _cache = cache;
        _state = state;
        _timeProvider = timeProvider;
        _lifetimeToken = _lifetime.Token;
        _state.Initialize(_updates.RestoreCachedResult(_cache.Load().LastSuccessfulResult));
    }

    public async Task ScheduleAutomaticCheckAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        await Task.Delay(delay, _timeProvider, cancellationToken);
        await CheckAsync(UpdateCheckMode.Automatic, cancellationToken);
    }

    public Task<UpdateCheckResult> CheckAsync(
        UpdateCheckMode mode,
        CancellationToken cancellationToken = default)
    {
        Task<UpdateCheckResult> sharedTask;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_inFlight is not null)
            {
                sharedTask = _inFlight;
            }
            else if (mode == UpdateCheckMode.Automatic && IsAutomaticCheckCached())
            {
                var restored = _updates.RestoreCachedResult(_cache.Load().LastSuccessfulResult)
                    ?? new UpdateCheckResult(UpdateCheckStatus.NotChecked, FromCache: true);
                if (restored.IsSuccessful)
                    _state.Initialize(restored);
                return Task.FromResult(restored);
            }
            else
            {
                var completion = new TaskCompletionSource<UpdateCheckResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight = completion.Task;
                sharedTask = _inFlight;
                _worker = ExecuteNetworkCheckAsync(completion, _lifetimeToken);
            }
        }

        return cancellationToken.CanBeCanceled
            ? sharedTask.WaitAsync(cancellationToken)
            : sharedTask;
    }

    private bool IsAutomaticCheckCached()
    {
        var lastAttempt = _cache.Load().LastAttemptAtUtc;
        if (lastAttempt is null) return false;
        var age = _timeProvider.GetUtcNow() - lastAttempt.Value;
        return age >= TimeSpan.Zero && age < AutomaticCheckInterval;
    }

    private async Task ExecuteNetworkCheckAsync(
        TaskCompletionSource<UpdateCheckResult> completion,
        CancellationToken lifetimeToken)
    {
        UpdateCheckResult result;
        try
        {
            _state.BeginCheck();
            var now = _timeProvider.GetUtcNow();
            _cache.SaveAttempt(now);
            var cached = _cache.Load().LastSuccessfulResult;
            result = await _updates.CheckLatestAsync(cached, lifetimeToken);
            if (result.IsSuccessful)
                result = result with { CheckedAtUtc = now };

            if (result.IsSuccessful &&
                result.Version is not null &&
                result.PublishedAt is not null &&
                result.ReleasePageUri is not null)
            {
                _cache.SaveSuccessfulResult(new UpdateCheckCache(
                    result.Version,
                    result.PublishedAt.Value,
                    result.ReleaseNotes ?? string.Empty,
                    result.ReleasePageUri.AbsoluteUri,
                    result.ETag,
                    now));
            }

            _state.Complete(result);
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(lifetimeToken);
        }
        catch (ObjectDisposedException) when (lifetimeToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(lifetimeToken);
        }
        catch (Exception)
        {
            result = new UpdateCheckResult(UpdateCheckStatus.Failed);
            try { _state.Complete(result); }
            catch { }
            completion.TrySetResult(result);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_inFlight, completion.Task))
                    _inFlight = null;
            }
        }
    }

    public void Dispose()
    {
        Task worker;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            worker = _worker;
        }
        _lifetime.Cancel();

        if (worker.IsCompleted)
        {
            ObserveWorkerAndDisposeLifetime(worker);
            return;
        }

        _ = worker.ContinueWith(
            completed => ObserveWorkerAndDisposeLifetime(completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ObserveWorkerAndDisposeLifetime(Task worker)
    {
        _ = worker.Exception;
        lock (_sync)
        {
            if (_lifetimeDisposed) return;
            _lifetimeDisposed = true;
        }
        _lifetime.Dispose();
    }
}
