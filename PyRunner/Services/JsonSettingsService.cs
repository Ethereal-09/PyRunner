using System.Text.Json;
using System.Text;
using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>
/// JSON 文件设置服务：%LOCALAPPDATA%\PyRunner\settings.json。
/// 写入防抖 500ms；读取异常（文件损坏/缺失）时回退默认值，保证启动不被阻塞。
/// </summary>
public sealed class JsonSettingsService : ISettingsService, IDisposable
{
    private const string DataDirectoryEnvironmentVariable = "PYRUNNER_DATA_DIRECTORY";
    private const string WriteMutexName = @"Local\PyRunner.Settings.Write.v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Mutex _writeMutex = new(false, WriteMutexName);
    private readonly Timer _debounceTimer;
    private AppSettings _settings;
    private bool _disposed;
    private long _revision;
    private long _persistedRevision = -1;

    public JsonSettingsService() : this(ResolveSettingsDirectory())
    {
    }

    internal JsonSettingsService(string settingsDirectory)
    {
        SettingsDirectory = Path.GetFullPath(settingsDirectory);
        _settings = Load();
        _persistedRevision = 0;
        _debounceTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string SettingsDirectory { get; }

    public string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
    public string BackupPath => SettingsPath + ".bak";

    private static string ResolveSettingsDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PyRunner")
            : Path.GetFullPath(overrideDirectory);
    }

    public AppSettings Current
    {
        get { lock (_lock) { return Clone(_settings); } }
    }

    public void Update(Action<AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var next = Clone(_settings);
            update(next);
            _settings = next;
            _revision++;
            _debounceTimer.Change(500, Timeout.Infinite);
        }
    }

    /// <summary>立即写入（取消防抖计时）。异常容错：写失败仅记日志不抛出。
    /// 序列化与文件 IO 均在锁内，消除防抖线程与关闭时 Flush 的乱序覆盖窗口。</summary>
    public void Flush()
    {
        try
        {
            FlushOrThrow();
        }
        catch (Exception ex)
        {
            LogDiagnostic("settings write failed", ex);
        }
    }

    public void FlushOrThrow()
    {
        AppSettings snapshot;
        long revision;
        lock (_lock)
        {
            if (_disposed) return;
            _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
            snapshot = Clone(_settings);
            revision = _revision;
        }

        PersistSnapshot(snapshot, revision);
    }

    private void PersistSnapshot(AppSettings snapshot, long revision)
    {
        _writeGate.Wait();
        try
        {
            lock (_lock)
            {
                if (revision <= _persistedRevision) return;
            }

            WriteSnapshot(snapshot);
            lock (_lock)
                _persistedRevision = Math.Max(_persistedRevision, revision);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>读取设置；文件缺失/损坏时返回默认值（默认 Language 跟随 CurrentUICulture）。</summary>
    private AppSettings Load()
    {
        var mutexHeld = EnterWriteMutex();
        try
        {
            if (!File.Exists(SettingsPath))
            {
                if (TryReadSettings(BackupPath, out var backupOnly))
                    return backupOnly;
                return new AppSettings();
            }

            if (TryReadSettings(SettingsPath, out var loaded))
                return loaded;

            IsolateCorruptSettings();
            LogDiagnostic("invalid settings JSON was isolated");
            if (TryReadSettings(BackupPath, out var backup))
            {
                TryRestoreBackup();
                return backup;
            }
        }
        catch (Exception ex)
        {
            LogDiagnostic("settings load failed; defaults will be used", ex);
        }
        finally
        {
            if (mutexHeld) _writeMutex.ReleaseMutex();
        }

        return new AppSettings();
    }

    private void WriteSnapshot(AppSettings snapshot)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var tempPath = Path.Combine(
            SettingsDirectory,
            $"settings.json.{Guid.NewGuid():N}.tmp");
        var mutexHeld = EnterWriteMutex();
        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(SettingsPath))
                File.Replace(tempPath, SettingsPath, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, SettingsPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { }
            }
            if (mutexHeld) _writeMutex.ReleaseMutex();
        }
    }

    private bool TryReadSettings(string path, out AppSettings settings)
    {
        settings = new AppSettings();
        if (!File.Exists(path)) return false;
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is null) return false;
            settings = loaded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private void IsolateCorruptSettings()
    {
        if (!File.Exists(SettingsPath)) return;
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var corruptPath = Path.Combine(
            SettingsDirectory,
            $"settings.json.{timestamp}.{Guid.NewGuid():N}.corrupt");
        File.Move(SettingsPath, corruptPath);
    }

    private void TryRestoreBackup()
    {
        var tempPath = Path.Combine(
            SettingsDirectory,
            $"settings.json.restore.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var source = new FileStream(BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }
            File.Move(tempPath, SettingsPath);
        }
        catch (Exception ex)
        {
            LogDiagnostic("settings backup could not be restored", ex);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch { }
            }
        }
    }

    private bool EnterWriteMutex()
    {
        try
        {
            if (_writeMutex.WaitOne(TimeSpan.FromSeconds(10))) return true;
            throw new TimeoutException("Timed out waiting for the settings write lock.");
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static AppSettings Clone(AppSettings source) => new()
    {
        SidebarWidth = source.SidebarWidth,
        UiLayoutVersion = source.UiLayoutVersion,
        SavedTreeExpanded = source.SavedTreeExpanded,
        Language = source.Language,
        Theme = source.Theme,
        TerminalAutoClear = source.TerminalAutoClear,
        NotifyOnFail = source.NotifyOnFail,
        LiveFlush = source.LiveFlush,
        AutoStart = source.AutoStart,
        AutoRefreshScripts = source.AutoRefreshScripts,
        FirstRunCompleted = source.FirstRunCompleted,
        FirstRunVersion = source.FirstRunVersion,
        LastUpdateCheckAttemptUtc = source.LastUpdateCheckAttemptUtc,
        LastSuccessfulUpdateCheck = source.LastSuccessfulUpdateCheck,
    };

    private void LogDiagnostic(string message, Exception? exception = null)
    {
        var suffix = exception is null ? string.Empty : $" ({exception.GetType().Name})";
        System.Diagnostics.Trace.TraceWarning($"PyRunner settings: {message}{suffix}");
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.AppendAllText(
                Path.Combine(SettingsDirectory, "settings-diagnostics.log"),
                $"[{DateTimeOffset.UtcNow:O}] {message}{suffix}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 诊断记录自身不得影响设置恢复或应用启动。
        }
#if DEBUG
        DebugLog.WriteLine($"Settings: {message}{suffix}");
#endif
    }

    public void Dispose()
    {
        AppSettings snapshot;
        long revision;
        lock (_lock)
        {
            if (_disposed) return;
            _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _disposed = true;
            snapshot = Clone(_settings);
            revision = _revision;
        }

        // 等待已经进入的计时器回调退出，避免其在写锁释放后访问已处置资源。
        using (var timerDisposed = new ManualResetEvent(false))
        {
            _debounceTimer.Dispose(timerDisposed);
            timerDisposed.WaitOne();
        }
        try { PersistSnapshot(snapshot, revision); }
        catch (Exception ex) { LogDiagnostic("final settings write failed", ex); }
        _writeGate.Dispose();
        _writeMutex.Dispose();
    }
}
