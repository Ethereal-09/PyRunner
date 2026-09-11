using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>应用存活期间的单定时器调度器；系统休眠恢复后会一次处理所有到期任务。</summary>
public sealed class ScheduleCoordinator : IDisposable
{
    private readonly IScheduledTaskService _tasks;
    private readonly IUiDispatcher _dispatcher;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;

    public ScheduleCoordinator(IScheduledTaskService tasks, IUiDispatcher dispatcher)
    {
        _tasks = tasks;
        _dispatcher = dispatcher;
    }

    public event Action<ScheduledTask>? TaskDue;

    public void Start() => Rearm();

    public void Rearm()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _timer?.Dispose();
            var next = _tasks.GetAll()
                .Where(t => t.Enabled && !string.IsNullOrWhiteSpace(t.NextRunAtUtc))
                .Select(t => DateTimeOffset.TryParse(t.NextRunAtUtc, out var value) ? value : (DateTimeOffset?)null)
                .Where(t => t.HasValue)
                .Min();
            if (next == null) { _timer = null; return; }
            var delay = next.Value - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
            _timer = new Timer(OnTimer, null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer(object? state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var task in _tasks.GetDue(now))
            {
                try
                {
                    _tasks.AdvanceAfterTrigger(task, now);
                    TaskDue?.Invoke(task);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ScheduleCoordinator: task {task.Id} failed ({ex.Message})");
                }
            }
            Rearm();
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
