namespace PyRunner.Services;

/// <summary>
/// 脚本目录监听服务契约（Phase E 交付 15）：每 ScriptPath 一个 FileSystemWatcher，
/// 磁盘变化（.py 新增/删除/改名、子目录增删）经 300ms 合并去抖后广播一次 <see cref="Changed"/>，
/// 由消费方（MainWindow）回 UI 线程重建树与列表；失效路径节点的重标记由树重建时的
/// Directory.Exists 检查完成。手动刷新为兜底路径（侧栏刷新按钮 → <see cref="Resync"/>）。
/// </summary>
public interface IScriptDirectoryWatcher : IDisposable
{
    /// <summary>去抖窗口结束后的变更广播（线程池线程触发，消费方自行封送 UI 线程）。</summary>
    event Action? Changed;

    /// <summary>按当前脚本路径集合对齐 watcher：新增补建、移除回收、失效路径跳过。</summary>
    void SyncWithPaths(IReadOnlyList<string> directoryPaths);

    /// <summary>从 ScriptPath 表读取当前路径集合并对齐 watcher（消费方无需自行查库）；
    /// 数据层故障时静默保持现状（手动刷新兜底）。</summary>
    void SyncFromStore();

    /// <summary>手动兜底刷新：强制重建全部 watcher（侧栏刷新按钮消费）。</summary>
    void Resync();
}

/// <summary>
/// 监听实现。关键取舍：
/// ① 不设 Filter（改名 .py→其它后缀时 Renamed 仅按新名匹配会漏报），改在事件回调内按扩展名判定；
/// ② 合并去抖 = 「窗口制」而非「顺延制」：首个事件开启 300ms 定时器，窗口内后续事件仅置脏标志，
///    避免持续写入下通知无限后移；
/// ③ Error 事件（多为内部缓冲溢出）→ 销毁并按原路径重建该 watcher，重建失败仅放弃该路径
///    （手动刷新仍可兜底），不让监听故障影响主链路。
/// </summary>
public sealed class ScriptDirectoryWatcher : IScriptDirectoryWatcher
{
    /// <summary>合并去抖窗口（任务书口径 300ms）。</summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(300);

    private readonly IScriptPathService _scriptPathService;
    private readonly object _lock = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _debounceTimer;
    private bool _dirty;
    private bool _disposed;

    public event Action? Changed;

    public ScriptDirectoryWatcher(IScriptPathService scriptPathService)
    {
        _scriptPathService = scriptPathService;
    }

    public void SyncFromStore()
    {
        IReadOnlyList<string> paths;
        try
        {
            paths = _scriptPathService.GetAll().Select(p => p.Path).ToList();
        }
        catch (Exception ex)
        {
            // 数据层故障：保持现状（手动刷新兜底），不影响主链路
            DebugWriteError("Watcher: 路径读取失败，保持现状", ex);
            return;
        }

        SyncWithPaths(paths);
    }

    public void SyncWithPaths(IReadOnlyList<string> directoryPaths)
    {
        lock (_lock)
        {
            if (_disposed) return;

            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in directoryPaths ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string full;
                try { full = Path.GetFullPath(raw.Trim()); }
                catch { continue; }
                if (!Directory.Exists(full)) continue; // 失效路径不建 watcher（树侧置灰由重建检查完成）
                wanted.Add(full);
            }

            // 回收不再需要的 watcher
            foreach (var stale in _watchers.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                DisposeWatcher(_watchers[stale]);
                _watchers.Remove(stale);
            }

            // 补建新增的 watcher（评审修 2：启用失败返回 null 不存字典，下一次 Sync/Resync 自然补建）
            foreach (var path in wanted)
                if (!_watchers.ContainsKey(path))
                {
                    var created = CreateWatcher(path);
                    if (created != null)
                        _watchers[path] = created;
                }
        }
    }

    public void Resync()
    {
        lock (_lock)
        {
            if (_disposed) return;
            var paths = _watchers.Keys.ToList();
            foreach (var watcher in _watchers.Values)
                DisposeWatcher(watcher);
            _watchers.Clear();
            foreach (var path in paths)
                if (Directory.Exists(path))
                {
                    var created = CreateWatcher(path);
                    if (created != null) // 评审修 2：启用失败不存字典，避免死壳占位阻断自愈
                        _watchers[path] = created;
                }
        }
    }

    /// <summary>创建并启用一个 watcher；启用失败（目录在判存与启用之间消失等竞态）
    /// 返回 null：调用方不存字典，让下一次 SyncWithPaths/Resync 自然补建（评审修 2：
    /// 旧口径塞空壳占位会令 ContainsKey 判定永远跳过补建，阻断自愈）。</summary>
    private FileSystemWatcher? CreateWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024, // 任务书口径 64KB，降低突发变更下的缓冲溢出概率
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
        };

        watcher.Created += OnFileSystemEvent;
        watcher.Deleted += OnFileSystemEvent;
        watcher.Renamed += OnRenamedEvent;
        watcher.Error += OnWatcherError;

        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // 直接放弃该路径：Dispose 真 watcher 且不存字典（手动刷新/下次同步兜底）
            DebugWriteError($"Watcher: 启用失败 {directory}", ex);
            DisposeWatcher(watcher);
            return null;
        }

        return watcher;
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        // 未设 Filter：仅 .py 文件相关变化参与触发（目录增删经 DirectoryName 通知无需过滤）
        if (e.Name?.EndsWith(".py", StringComparison.OrdinalIgnoreCase) != true) return;
        MarkDirty();
    }

    private void OnRenamedEvent(object sender, RenamedEventArgs e)
    {
        // 任一侧为 .py 即视为脚本集合变化（.py→txt 删除语义、txt→.py 新增语义）
        if (e.Name?.EndsWith(".py", StringComparison.OrdinalIgnoreCase) == true ||
            e.OldName?.EndsWith(".py", StringComparison.OrdinalIgnoreCase) == true)
            MarkDirty();
    }

    /// <summary>Error（多为缓冲溢出）：销毁并按原路径重建；重建失败放弃该路径（Resync 兜底）。</summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (sender is not FileSystemWatcher failed) return;

            var path = failed.Path;
            DebugWriteError($"Watcher: Error 事件，重建 {path}", e.GetException());

            DisposeWatcher(failed);
            _watchers.Remove(path);
            if (Directory.Exists(path))
            {
                var rebuilt = CreateWatcher(path);
                if (rebuilt != null) // 评审修 2：重建失败放弃该路径（Resync 兜底），不留死壳
                    _watchers[path] = rebuilt;
            }
        }

        // 错误通常伴随一批丢失的事件：触发一次刷新广播，保证视图最终一致
        MarkDirty();
    }

    /// <summary>窗口制合并去抖：首个事件开定时器，窗口内后续事件仅置脏。</summary>
    private void MarkDirty()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _dirty = true;
            if (_debounceTimer == null)
            {
                _debounceTimer = new Timer(OnDebounceElapsed, null, DebounceWindow, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        lock (_lock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            if (!_dirty || _disposed) return;
            _dirty = false;
        }

        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            DebugWriteError("Watcher: 变更广播消费失败", ex);
        }
    }

    private static void DisposeWatcher(FileSystemWatcher watcher)
    {
        // 先停事件源再 Dispose，防回调续发；事件退订随 Dispose 生效
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            foreach (var watcher in _watchers.Values)
                watcher.Dispose();
            _watchers.Clear();
        }
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugWriteError(string context, Exception ex)
    {
#if DEBUG
        DebugLog.WriteLine($"{context}（{ex.Message}）");
#endif
    }
}
