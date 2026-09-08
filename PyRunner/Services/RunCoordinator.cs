using Microsoft.UI.Dispatching;
using PyRunner.Models;
using PyRunner.ViewModels;

namespace PyRunner.Services;

/// <summary>TryStart 结果：成功返回待挂标签的会话 VM；失败返回本地化错误键。</summary>
public readonly record struct TryStartResult(TerminalSessionViewModel? ViewModel, string? ErrorKey)
{
    public bool IsSuccess => ViewModel != null;
}

/// <summary>
/// 运行编排（Phase D 交付 13）：单例级协调全部运行中标签。职责：
/// 解释器解析（脚本指定 → 默认解释器 → 无则引导设置）、脚本存在性前置检查、
/// 同脚本禁止并发、命令行拼装（路径引号规则）、venv 环境变量计算、
/// 停止编排（spike 实证 \x03 可达：<c>\x03 → 2s 超时 → Dispose/Kill</c>，状态映射 Killed）、
/// 输出旁路累积写 RunRecord（200KB 环形尾部截断，DB 状态 running/success/failed/killed，
/// 每脚本保留最近 10 条由 RunRecordService 负责）。
/// 线程约定：全部入口与事件回调均在 UI 线程（PtySession 事件已封送），无需加锁。
/// </summary>
public sealed class RunCoordinator
{
    /// <summary>停止编排：\x03 发出后等待优雅退出的时限（秒）。</summary>
    private const double StopGraceSeconds = 2.0;

    /// <summary>Restart 编排（评审修 4）：等待运行终结的时限（秒）。
    /// RequestStop 自带 2s 宽限 + 强制 Kill，Kill 后 Exited 迅速到达，5s 足够覆盖异常路径。</summary>
    private const double RestartWaitSeconds = 5.0;

    private readonly IRunRecordService _runRecords;
    private readonly IInterpreterService _interpreters;
    private readonly ILocalizationService _localization;
    private readonly ISettingsService _settings;

    private sealed class ActiveRun
    {
        public required TerminalSessionViewModel ViewModel { get; init; }
        public required TailOutputBuffer Buffer { get; init; }
        public required int RecordId { get; init; }

        /// <summary>用户已请求停止（或关闭了运行中标签）：退出时状态映射 Killed。</summary>
        public bool StopRequested { get; set; }

        public DispatcherQueueTimer? StopTimer { get; set; }
        public bool Finalized { get; set; }
    }

    /// <summary>运行中会话索引（ScriptId → ActiveRun）；同脚本禁止并发的真相源。</summary>
    private readonly Dictionary<int, ActiveRun> _activeByScriptId = new();

    /// <summary>Restart 编排的一次性终结等待订阅（评审修 4）：Finalize 时完成。</summary>
    private sealed class ExitWaiter
    {
        public required TaskCompletionSource<bool> Tcs { get; init; }
        public DispatcherQueueTimer? Timer { get; set; }
    }

    private readonly Dictionary<int, List<ExitWaiter>> _exitWaiters = new();

    public RunCoordinator(
        IRunRecordService runRecords,
        IInterpreterService interpreters,
        ILocalizationService localization,
        ISettingsService settings)
    {
        _runRecords = runRecords;
        _interpreters = interpreters;
        _localization = localization;
        _settings = settings;
    }

    /// <summary>脚本运行状态变化（供侧栏徽章状态机消费；UI 线程）。</summary>
    public event Action<int, RunStatus>? StatusChanged;

    /// <summary>查询某脚本是否正在运行。</summary>
    public bool IsRunning(int scriptId) => _activeByScriptId.ContainsKey(scriptId);

    /// <summary>
    /// 启动一次运行。成功返回会话 VM（调用方据此创建标签并 InitializeAsync）；
    /// 失败返回错误键（调用方本地化展示），不抛业务异常。
    /// </summary>
    public TryStartResult TryStart(Script script)
    {
        // 1. 同脚本禁止并发
        if (_activeByScriptId.ContainsKey(script.Id))
            return new TryStartResult(null, "Run_AlreadyRunning");

        // 2. 脚本存在性前置检查
        if (!File.Exists(script.FilePath))
            return new TryStartResult(null, "Run_ScriptNotFound");

        // 2.5 启动参数引号配对兜底（评审修复 4）：奇数个双引号会把脚本路径吞进参数，
        // 登记对话框已有字段级校验，此处防御存量脏数据/旁路写入
        if (!string.IsNullOrEmpty(script.Arguments) &&
            script.Arguments.Count(ch => ch == '"') % 2 != 0)
            return new TryStartResult(null, "Run_ArgumentsQuoteMismatch");

        // 3. 解释器解析：脚本指定 → 默认解释器 → 无则引导设置
        Interpreter? interpreter = null;
        if (script.InterpreterId is int specifiedId)
        {
            interpreter = _interpreters.GetAll().FirstOrDefault(i => i.Id == specifiedId);
        }
        interpreter ??= _interpreters.GetDefault();
        if (interpreter == null)
            return new TryStartResult(null, "Run_NoInterpreter");
        if (!File.Exists(interpreter.ExecutablePath))
            return new TryStartResult(null, "Run_InterpreterMissing");

        // 4. venv 判定与环境变量（VIRTUAL_ENV/PATH 由本协调器算好，经 extraEnvironment 传入）
        var extraEnvironment = BuildVenvEnvironment(interpreter.ExecutablePath);

        // 5. 命令行拼装（路径引号规则）+ 工作目录（未指定 = 脚本所在目录）
        var commandLine = PythonCommandLine.Build(
            interpreter.ExecutablePath, script.FilePath, script.Arguments);
        var workingDirectory = !string.IsNullOrWhiteSpace(script.WorkingDirectory) && Directory.Exists(script.WorkingDirectory)
            ? script.WorkingDirectory
            : Path.GetDirectoryName(script.FilePath);

        // 6. 落库 running 记录（含超额清理，每脚本保留 10 条）
        int recordId;
        try
        {
            recordId = _runRecords.StartRun(script.Id);
        }
        catch (Exception ex)
        {
            // 落库失败：不得带病启动（否则退出时无 record 可终结）
            DebugWriteError($"Run: StartRun 落库失败 scriptId={script.Id}", ex);
            return new TryStartResult(null, "Error_SaveFailed");
        }

        ActiveRun run;
        try
        {
            var viewModel = new TerminalSessionViewModel(
                _localization, commandLine, script.Id, script.Name,
                workingDirectory, extraEnvironment,
                liveFlush: _settings.Current.LiveFlush,
                autoClearOnExit: _settings.Current.TerminalAutoClear,
                displayCommand: BuildDisplayCommand(script));

            run = new ActiveRun
            {
                ViewModel = viewModel,
                Buffer = new TailOutputBuffer(),
                RecordId = recordId,
            };

            // 输出旁路：base64 → 原始字节 → 200KB 环形缓冲（线程安全，事件已在 UI 线程）
            viewModel.OutputProduced += base64 => AppendOutput(run, base64);
            viewModel.Exited += code => OnSessionExited(run, code);
            viewModel.StartFailed += _ => OnStartFailed(run);
        }
        catch (Exception ex)
        {
            // 评审修复 10：StartRun 落库后到字典登记之间的异常 → 回滚 running 记录，
            // 不残留永久 running 的僵尸行
            DebugWriteError($"Run: 会话构造失败，回滚 running 记录 scriptId={script.Id}", ex);
            try { _runRecords.FinishRun(recordId, null, RunStatus.Failed, string.Empty); }
            catch (Exception ex2) { DebugWriteError($"Run: 回滚 FinishRun 失败 recordId={recordId}", ex2); }
            return new TryStartResult(null, "Error_SaveFailed");
        }

        _activeByScriptId[script.Id] = run;
        StatusChanged?.Invoke(script.Id, RunStatus.Running);
#if DEBUG
        DebugLog.WriteLine($"Run: 启动 scriptId={script.Id} recordId={recordId} cmd={commandLine} cwd={workingDirectory} venv={(extraEnvironment != null)}");
#endif
        return new TryStartResult(run.ViewModel, null);
    }

    /// <summary>
    /// 停止编排（spike 结论：\x03 可达）：写入 ETX 触发 KeyboardInterrupt，
    /// 2s 宽限内未退出则 ForceKill（Dispose → Job Object 连带杀死进程树），状态映射 Killed。
    /// </summary>
    public void RequestStop(int scriptId)
    {
        if (!_activeByScriptId.TryGetValue(scriptId, out var run) || run.Finalized) return;
        run.StopRequested = true;
        run.ViewModel.RequestInterrupt();

        // 2s 超时兜底：进程吞掉 KBI（while True 且未设检查点）时强制拆卸
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(StopGraceSeconds);
        timer.Tick += (t, _) =>
        {
            t.Stop();
            if (!_activeByScriptId.ContainsKey(scriptId) || run.Finalized) return;
#if DEBUG
            DebugLog.WriteLine($"Run: \\x03 超时 {StopGraceSeconds}s，强制 Kill scriptId={scriptId}");
#endif
            run.ViewModel.ForceKill();
            // ForceKill 后 waiter 线程仍会补发 Exited（Dispose 路径），经 OnSessionExited 幂等落库；
            // 若极端情况下事件未达（Join 超时），定时器侧直接兜底终结
            if (!run.Finalized)
                FinalizeDeferred(run, scriptId, exitCode: null, RunStatus.Killed);
        };
        timer.Start();
        run.StopTimer = timer;
    }

    /// <summary>
    /// Restart 编排——停止并等待终结（评审修 4）：RequestStop + 一次性订阅该运行的终结事件。
    /// 返回 true = 已终结（调用方据此自行 TryStart，失败走既有错误通知路径）；
    /// 返回 false = 等待超时（调用方不得强重跑，停止编排的强制 Kill 仍会兜底）。
    /// 超时由 DispatcherQueueTimer 回收；窗口关闭经 ShutdownAll 退订全部订阅（完成 false）。
    /// </summary>
    public Task<bool> StopAndWaitExitAsync(int scriptId)
    {
        if (!_activeByScriptId.ContainsKey(scriptId))
            return Task.FromResult(true);

        var waiter = new ExitWaiter
        {
            Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        if (!_exitWaiters.TryGetValue(scriptId, out var list))
            _exitWaiters[scriptId] = list = new List<ExitWaiter>();
        list.Add(waiter);

        RequestStop(scriptId);

        // 超时回收：终结事件极端不达（Join 超时等）时不挂死编排链
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(RestartWaitSeconds);
        timer.Tick += (t, _) =>
        {
            t.Stop();
            if (waiter.Tcs.Task.IsCompleted) return;
            RemoveExitWaiter(scriptId, waiter);
#if DEBUG
            DebugLog.WriteLine($"Run: Restart 等待终结超时 {RestartWaitSeconds}s，放弃重跑 scriptId={scriptId}");
#endif
            waiter.Tcs.TrySetResult(false);
        };
        timer.Start();
        waiter.Timer = timer;

        return waiter.Tcs.Task;
    }

    private void RemoveExitWaiter(int scriptId, ExitWaiter waiter)
    {
        if (_exitWaiters.TryGetValue(scriptId, out var list))
        {
            list.Remove(waiter);
            if (list.Count == 0)
                _exitWaiters.Remove(scriptId);
        }
    }

    /// <summary>关闭标签（含运行中）：运行中的会话按停止处理（状态 Killed），随后释放 VM。
    /// 评审修复 9：Kill 落库延迟一拍（DispatcherQueue 排空一轮），
    /// 让被杀进程已入队的末段输出（如 traceback）先进缓冲再落库。</summary>
    public void CloseSession(TerminalSessionViewModel viewModel)
    {
        if (_activeByScriptId.TryGetValue(viewModel.ScriptId, out var run) && !run.Finalized)
        {
            run.StopRequested = true;
            FinalizeDeferred(run, viewModel.ScriptId, exitCode: null, RunStatus.Killed);
        }
        viewModel.Shutdown();
    }

    /// <summary>进程从未启动的终止入口（评审修复 3）：WebView2 初始化失败时
    /// 未终结的 run 映射 Failed（与 StartFailed 路径对齐），再 Shutdown 释放 VM。</summary>
    public void AbortNotStarted(TerminalSessionViewModel viewModel)
    {
        if (_activeByScriptId.TryGetValue(viewModel.ScriptId, out var run) && !run.Finalized)
            Finalize(run, viewModel.ScriptId, exitCode: null, RunStatus.Failed);
        viewModel.Shutdown();
    }

    /// <summary>窗口关闭：释放全部会话（Shutdown 内 Dispose 触发 Job Object 孤儿防护）。</summary>
    public void ShutdownAll()
    {
        foreach (var run in _activeByScriptId.Values)
        {
            run.StopTimer?.Stop();
            if (!run.Finalized)
                Finalize(run, run.ViewModel.ScriptId, exitCode: null, RunStatus.Killed);
            run.ViewModel.Shutdown();
        }
        _activeByScriptId.Clear();

        // 窗口关闭：退订全部 Restart 终结等待订阅（评审修 4，完成 false 使调用方不重跑）
        foreach (var list in _exitWaiters.Values)
        {
            foreach (var w in list)
            {
                w.Timer?.Stop();
                w.Tcs.TrySetResult(false);
            }
        }
        _exitWaiters.Clear();
    }

    // ---- 内部实现 ----

    private void AppendOutput(ActiveRun run, string base64)
    {
        if (run.Finalized) return;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            run.Buffer.Append(bytes, 0, bytes.Length);
        }
        catch (FormatException)
        {
            // 非法 base64（协议不可能产生）：跳过该块，不中断运行
        }
    }

    private void OnSessionExited(ActiveRun run, int exitCode)
    {
        if (run.Finalized || !_activeByScriptId.ContainsKey(run.ViewModel.ScriptId)) return;

        var status = run.StopRequested
            ? RunStatus.Killed
            : exitCode == 0 ? RunStatus.Success : RunStatus.Failed;
        Finalize(run, run.ViewModel.ScriptId, exitCode, status);
    }

    private void OnStartFailed(ActiveRun run)
    {
        if (run.Finalized) return;
        Finalize(run, run.ViewModel.ScriptId, exitCode: null, RunStatus.Failed);
    }

    /// <summary>Kill 路径延迟一拍终结（评审修复 9）：让已入 DispatcherQueue 的末段输出
    /// 先经 OutputProduced 进缓冲；入队失败（窗口关闭 dispatcher 停摆）则同步兜底。</summary>
    private void FinalizeDeferred(ActiveRun run, int scriptId, int? exitCode, RunStatus status)
    {
        var dq = DispatcherQueue.GetForCurrentThread();
        if (dq == null || !dq.TryEnqueue(() => Finalize(run, scriptId, exitCode, status)))
            Finalize(run, scriptId, exitCode, status);
    }

    /// <summary>终结一次运行：落库 + 索引清理 + 状态广播（幂等：Finalized 标志防重复）。</summary>
    private void Finalize(ActiveRun run, int scriptId, int? exitCode, RunStatus status)
    {
        if (run.Finalized) return;
        run.Finalized = true;
        run.StopTimer?.Stop();
        _activeByScriptId.Remove(scriptId);

        try
        {
            _runRecords.FinishRun(run.RecordId, exitCode, status, run.Buffer.GetText());
        }
        catch (Exception ex)
        {
            // 数据层故障不得打断退出流程：仅记日志（RunRecordService.FinishRun 自身对缺记录已容错）
            DebugWriteError($"Run: FinishRun 失败 scriptId={scriptId}", ex);
        }

        StatusChanged?.Invoke(scriptId, status);

        // Restart 编排：完成该脚本的一次性终结等待订阅（评审修 4）
        if (_exitWaiters.TryGetValue(scriptId, out var waiters))
        {
            _exitWaiters.Remove(scriptId);
            foreach (var w in waiters)
            {
                w.Timer?.Stop();
                w.Tcs.TrySetResult(true);
            }
        }
#if DEBUG
        DebugLog.WriteLine($"Run: 终结 scriptId={scriptId} status={status} exitCode={exitCode}");
#endif
    }

    /// <summary>
    /// venv 判定：解释器所在目录或其上级目录存在 pyvenv.cfg 为准
    /// （目录名为 Scripts 时先查上级目录作快路径）。命中则返回 VIRTUAL_ENV + 前置 PATH；
    /// 非 venv 返回 null（不注入额外环境）。
    /// </summary>
    private static IReadOnlyDictionary<string, string>? BuildVenvEnvironment(string interpreterPath)
    {
        var exeDir = Path.GetDirectoryName(interpreterPath);
        if (string.IsNullOrEmpty(exeDir)) return null;

        string? venvRoot = null;

        // 快路径：标准布局 venv\Scripts\python.exe → 上级目录即 venv 根
        if (Path.GetFileName(exeDir).Equals("Scripts", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(exeDir);
            if (parent != null && File.Exists(Path.Combine(parent, "pyvenv.cfg")))
                venvRoot = parent;
        }

        // 权威规则：pyvenv.cfg 存在为准（解释器目录自身 → 上级目录）
        if (venvRoot == null)
        {
            if (File.Exists(Path.Combine(exeDir, "pyvenv.cfg")))
                venvRoot = exeDir;
            else
            {
                var parent = Path.GetDirectoryName(exeDir);
                if (parent != null && File.Exists(Path.Combine(parent, "pyvenv.cfg")))
                    venvRoot = parent;
            }
        }

        if (venvRoot == null) return null;

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["VIRTUAL_ENV"] = venvRoot,
            ["PATH"] = Path.Combine(venvRoot, "Scripts") + ";" + pathValue,
        };
    }

    /// <summary>终端首行仅展示稳定、易读的命令；真实启动仍使用含解释器绝对路径的 commandLine。</summary>
    private static string BuildDisplayCommand(Script script)
    {
        var fileName = Path.GetFileName(script.FilePath);
        if (fileName.Contains(' ')) fileName = $"\"{fileName}\"";
        return string.IsNullOrWhiteSpace(script.Arguments)
            ? $"> python {fileName}"
            : $"> python {fileName} {script.Arguments}";
    }

    /// <summary>DEBUG 日志辅助：Release 下调用点被编译器移除，避免未使用变量警告。</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugWriteError(string context, Exception ex)
    {
#if DEBUG
        DebugLog.WriteLine($"{context}（{ex.Message}）");
#endif
    }
}
