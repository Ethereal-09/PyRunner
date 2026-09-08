using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using static PyRunner.Terminal.ConPtyNative;

namespace PyRunner.Terminal;

/// <summary>
/// ConPTY 伪终端会话：创建伪终端与两对管道，启动子进程（python.exe），
/// 通过 Job Object 提供孤儿进程防护，并把输出/退出事件封送到 UI 线程。
/// </summary>
public sealed class PtySession : IDisposable
{
    private const int ReadBufferSize = 16 * 1024;

    /// <summary>输入队列上限：防止子进程不读 stdin 时粘贴大文本无限堆积。</summary>
    private const int WriteQueueBound = 256;

    private readonly string _commandLine;
    private readonly Func<Action, bool> _dispatch;

    /// <summary>可选工作目录（CreateProcessW lpCurrentDirectory）；null = 继承父进程（与历史行为等价）。</summary>
    private readonly string? _workingDirectory;

    /// <summary>可选额外环境变量（并入 Unicode 环境块，覆盖同名键）；null = 不注入（与历史行为等价）。</summary>
    private readonly IReadOnlyDictionary<string, string>? _extraEnvironment;

    /// <summary>
    /// 句柄拆卸锁：waiter / reader / Dispose 三条路径共用，
    /// 统一「锁内取值清零、锁外关闭」模式，杜绝 double-close 与句柄复用风险。
    /// </summary>
    private readonly object _handleLock = new();

    private short _cols;
    private short _rows;

    private IntPtr _hPC;        // 伪终端句柄
    private IntPtr _conInRead;  // ConPTY 端：输入管道读端（子进程创建后关闭）
    private IntPtr _conInWrite; // 本进程端：写入后成为子进程 stdin
    private IntPtr _conOutRead; // 本进程端：读取子进程 stdout（ConPTY 渲染后的字节流）
    private IntPtr _conOutWrite; // ConPTY 端：输出管道写端（子进程创建后关闭）
    private IntPtr _attrList;   // PROC_THREAD_ATTRIBUTE_LIST
    private IntPtr _job;        // Job Object 句柄
    private IntPtr _process;    // 子进程句柄

    private Thread? _readerThread;
    private Thread? _waiterThread;
    private Thread? _writerThread;
    private BlockingCollection<byte[]>? _inputQueue;

    /// <summary>原子防重入标志：0=未释放，1=已释放。</summary>
    private int _disposedFlag;

    private bool IsDisposed => Thread.VolatileRead(ref _disposedFlag) != 0;

    /// <summary>ConPTY 输出（Base64 编码的原始字节），已封送到 UI 线程。</summary>
    public event Action<string>? OutputReceived;

    /// <summary>子进程退出（退出码），已封送到 UI 线程。</summary>
    public event Action<int>? ProcessExited;

    public PtySession(
        string commandLine,
        short cols,
        short rows,
        Func<Action, bool> dispatch,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        _commandLine = commandLine;
        _cols = cols;
        _rows = rows;
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        _workingDirectory = workingDirectory;
        _extraEnvironment = extraEnvironment;
    }

    /// <summary>
    /// 创建伪终端并启动子进程。中途任何一步失败都会按逆序回滚已分配的全部资源
    /// （经 Dispose，其内部对未分配资源均有空/零防护）后重新抛出异常。
    /// </summary>
    public void Start()
    {
        try
        {
            StartCore();
        }
        catch (Exception ex)
        {
            _ = ex;
#if DEBUG
            DebugLog.WriteLine($"Pty: Start 失败，回滚已分配资源（{ex}）");
#endif
            Dispose();
            throw;
        }
    }

    private void StartCore()
    {
        // 1. 两对匿名管道（字节模式）：conIn 供我们写入、conOut 供我们读取
        if (!CreatePipe(out _conInRead, out _conInWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建 ConPTY 输入管道失败");

        if (!CreatePipe(out _conOutRead, out _conOutWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "创建 ConPTY 输出管道失败");
#if DEBUG
        DebugLog.WriteLine($"Pty: pipes conInRead=0x{_conInRead:X} conInWrite=0x{_conInWrite:X} conOutRead=0x{_conOutRead:X} conOutWrite=0x{_conOutWrite:X}");
#endif

        // 2. 创建伪终端：conInRead 交给 ConPTY 作为输入端，conOutWrite 作为输出端；
        //    这两端由 ConPTY 内部复制，本进程不再持有
        var hr = CreatePseudoConsole(new COORD { X = _cols, Y = _rows }, _conInRead, _conOutWrite, 0, out _hPC);
#if DEBUG
        DebugLog.WriteLine($"Pty: CreatePseudoConsole hr=0x{hr:X8} hPC=0x{_hPC:X} size={_cols}x{_rows}");
#endif
        if (hr != 0)
            throw new InvalidOperationException($"CreatePseudoConsole 失败: 0x{hr:X8}");

        // 3. 扩展启动属性列表：关联 PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE
        var attrSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize); // 第一次调用获取所需大小（预期失败）
        _attrList = Marshal.AllocHGlobal(attrSize);
        if (!InitializeProcThreadAttributeList(_attrList, 1, 0, ref attrSize))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList 失败");
        if (!UpdateProcThreadAttributeList(_attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute 失败");

        // 4. Job Object：关闭句柄时杀死全部关联进程（KILL_ON_JOB_CLOSE）
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject 失败");

        var jobInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };
        if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref jobInfo, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject 失败");

        // 5. Unicode 环境块：继承当前环境并强制 UTF-8（可选并入调用方提供的额外变量）
        var envPtr = Marshal.StringToHGlobalUni(BuildUnicodeEnvironmentBlock(_extraEnvironment));

        // 6. 创建子进程（STARTUPINFOEX + EXTENDED_STARTUPINFO_PRESENT）
        var startupInfo = new STARTUPINFOEXW
        {
            StartupInfo = new STARTUPINFOW
            {
                cb = (uint)Marshal.SizeOf<STARTUPINFOEXW>(),
            },
            lpAttributeList = _attrList
        };
        var securityAttributeSize = Marshal.SizeOf<SECURITY_ATTRIBUTES>();
        var processSecurity = new SECURITY_ATTRIBUTES { nLength = securityAttributeSize };
        var threadSecurity = new SECURITY_ATTRIBUTES { nLength = securityAttributeSize };

        bool created;
        PROCESS_INFORMATION processInfo = default;
        try
        {
            created = CreateProcessW(
                lpApplicationName: null,
                lpCommandLine: _commandLine,
                lpProcessAttributes: ref processSecurity,
                lpThreadAttributes: ref threadSecurity,
                bInheritHandles: false,
                dwCreationFlags: EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                lpEnvironment: envPtr,
                lpCurrentDirectory: _workingDirectory,
                lpStartupInfo: ref startupInfo,
                lpProcessInformation: out processInfo);
        }
        finally
        {
            Marshal.FreeHGlobal(envPtr);
        }

        if (!created)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess 失败（命令行：{_commandLine}）");

        // 保持 ConPTY 侧管道端至少活到子进程完成附着；随后本进程只保留宿主侧读/写端。
        var ptyInput = TakeHandle(ref _conInRead);
        if (ptyInput != IntPtr.Zero) CloseHandle(ptyInput);
        var ptyOutput = TakeHandle(ref _conOutWrite);
        if (ptyOutput != IntPtr.Zero) CloseHandle(ptyOutput);

        _process = processInfo.hProcess;
#if DEBUG
        DebugLog.WriteLine($"Pty: CreateProcess 成功 pid={processInfo.dwProcessId} hProcess=0x{_process:X}");
#endif

        // 7. 立即纳入 Job Object（孤儿防护的唯一机制）：失败则快速失败，不带病运行
        if (!AssignProcessToJobObject(_job, _process))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject 失败，孤儿防护不可用");

        // 8. 后台线程：输入写入 + 输出读取 + 进程退出等待
        _inputQueue = new BlockingCollection<byte[]>(WriteQueueBound);
        _writerThread = new Thread(InputWriteLoop) { IsBackground = true, Name = "PyRunner-PtyWriter" };
        _writerThread.Start();
        _readerThread = new Thread(OutputReadLoop) { IsBackground = true, Name = "PyRunner-PtyReader" };
        _readerThread.Start();
        _waiterThread = new Thread(ProcessWaitLoop) { IsBackground = true, Name = "PyRunner-PtyWaiter" };
        _waiterThread.Start();

        // 子进程挂入 Job 且宿主读写线程均就绪后才恢复主线程：避免极短命进程在
        // Job 纳管前派生子进程，也避免交互程序在输入写端消费线程就绪前读取 stdin。
        var resumeResult = ResumeThread(processInfo.hThread);
        CloseHandle(processInfo.hThread);
        if (resumeResult == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread 失败");
#if DEBUG
        DebugLog.WriteLine("Pty: 写/读/等待线程已启动");
#endif
    }

    /// <summary>
    /// 前端键盘输入 → UTF-8 字节 → 有界输入队列 → 后台写线程落盘 ConPTY 输入管道。
    /// UI 线程只做入队（队列满时直接丢弃），不阻塞在 WriteFile 上。
    /// 单写线程顺序消费，保持输入顺序与字节语义。
    /// </summary>
    public void WriteInput(string text)
    {
        if (IsDisposed || string.IsNullOrEmpty(text)) return;

        var queue = _inputQueue;
        if (queue == null || queue.IsAddingCompleted) return;

        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            if (!queue.TryAdd(bytes))
            {
#if DEBUG
                DebugLog.WriteLine("Pty: 输入队列已满，丢弃本次输入");
#endif
            }
        }
        catch (InvalidOperationException)
        {
            // 并发 CompleteAdding（Dispose 已开始），静默丢弃
        }
    }

    /// <summary>
    /// 后台写线程：启动时经 TakeHandle 取走输入管道句柄（此后 Dispose 不会重复关闭），
    /// 顺序消费输入队列逐块 WriteFile，退出时在 finally 中关闭句柄。
    /// </summary>
    private void InputWriteLoop()
    {
        var queue = _inputQueue;
        var handle = TakeHandle(ref _conInWrite);

        try
        {
            if (queue != null && handle != IntPtr.Zero)
            {
                foreach (var bytes in queue.GetConsumingEnumerable())
                {
                    var ok = WriteFile(handle, bytes, (uint)bytes.Length, out var written, IntPtr.Zero);
#if DEBUG
                    DebugLog.WriteLine($"Pty: WriteInput ok={ok} written={written}/{bytes.Length} err={(ok ? 0 : Marshal.GetLastWin32Error())}");
#endif
                    if (!ok) break; // 管道断开（会话结束），停止写入
                }
            }
        }
        catch
        {
#if DEBUG
            DebugLog.WriteLine("Pty: 写线程异常（句柄可能在拆卸阶段被关闭）");
#endif
        }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
#if DEBUG
            DebugLog.WriteLine("Pty: 写线程退出");
#endif
        }
    }

    /// <summary>前端窗口尺寸变化 → ResizePseudoConsole（锁内取值，避免 check-then-use 竞争）。</summary>
    public void Resize(short cols, short rows)
    {
        if (IsDisposed) return;
        if (cols <= 0 || rows <= 0) return;
        if (cols == _cols && rows == _rows) return;

        _cols = cols;
        _rows = rows;

        IntPtr handle;
        lock (_handleLock) { handle = _hPC; }
        if (handle != IntPtr.Zero)
            ResizePseudoConsole(handle, new COORD { X = cols, Y = rows });
    }

    /// <summary>锁内取值并清零句柄，锁外由调用方关闭；返回 Zero 表示已被其他路径关闭。</summary>
    private IntPtr TakeHandle(ref IntPtr field)
    {
        lock (_handleLock)
        {
            var handle = field;
            field = IntPtr.Zero;
            return handle;
        }
    }

    private void OutputReadLoop()
    {
        var buffer = new byte[ReadBufferSize];
        IntPtr handle;
        lock (_handleLock) { handle = _conOutRead; }
#if DEBUG
        DebugLog.WriteLine($"Pty: 读线程启动，等待 ReadFile(0x{handle:X})");
#endif
        try
        {
            while (!IsDisposed)
            {
                var ok = ReadFile(handle, buffer, (uint)buffer.Length, out var bytesRead, IntPtr.Zero);
#if DEBUG
                if (!ok)
                {
                    DebugLog.WriteLine($"Pty: ReadFile 失败 err={Marshal.GetLastWin32Error()}");
                }
                else
                {
                    DebugLog.WriteLine($"Pty: ReadFile ok={ok} bytes={bytesRead}");
                }
#endif
                if (!ok || bytesRead == 0)
                    break; // 管道断开 / EOF

                var base64 = Convert.ToBase64String(buffer, 0, (int)bytesRead);
                if (!_dispatch(() => OutputReceived?.Invoke(base64)))
                {
#if DEBUG
                    DebugLog.WriteLine("Pty: TryEnqueue 失败，读线程退出");
#endif
                    break; // Dispatcher 已关闭
                }
            }
        }
        catch
        {
#if DEBUG
            DebugLog.WriteLine("Pty: 读线程异常（句柄可能在拆卸阶段被关闭）");
#endif
            // 句柄可能在拆卸阶段被关闭，静默退出
        }
        finally
        {
#if DEBUG
            DebugLog.WriteLine("Pty: 读线程退出");
#endif
            // 与 Dispose 共用 TakeHandle：只有一方拿到句柄，杜绝 double-close
            var owned = TakeHandle(ref _conOutRead);
            if (owned != IntPtr.Zero) CloseHandle(owned);
        }
    }

    /// <summary>
    /// 进程退出等待线程。正常退出时不立即发 exit：先等输出读线程把管道排空
    /// （主动关闭伪终端使 conhost 退出、管道写端关闭，ReadFile 读到 0 字节后
    /// 读线程自然结束），再触发 exit 事件，保证 exit 消息排在全部输出之后
    /// （两者均经 DispatcherQueue 串行投递，读线程先入队的输出必然先送达）。
    /// </summary>
    private void ProcessWaitLoop()
    {
        IntPtr process;
        lock (_handleLock) { process = _process; }
        if (process == IntPtr.Zero) return;

        WaitForSingleObject(process, INFINITE);

        var exitCode = 0;
        if (GetExitCodeProcess(process, out var code))
            exitCode = unchecked((int)code);

        // 与 Dispose 共用 TakeHandle：只有一方关闭进程句柄
        if (TakeHandle(ref _process) != IntPtr.Zero)
            CloseHandle(process);

        // Dispose（用户关闭窗口）路径不等待排空，尽快退出；
        // 仅在正常退出路径等待读线程结束，超时 2 秒兜底（防管道未关闭时永久阻塞）
        if (!IsDisposed)
        {
            // 子进程已退出，但 ConPTY（conhost --headless）仍持有输出管道写端，
            // 读线程会永久阻塞在 ReadFile。主动关闭伪终端：conhost 随之退出、
            // 管道写端全部关闭，读线程读到 0 字节后自然结束，排空即完成。
            // （已排空的最后一段输出不会丢失：读线程在管道关闭前仍会读完）
            var hPc = TakeHandle(ref _hPC);
            if (hPc != IntPtr.Zero)
                ClosePseudoConsole(hPc);

            var drained = _readerThread?.Join(TimeSpan.FromSeconds(2)) ?? true;
#if DEBUG
            DebugLog.WriteLine($"Pty: 进程退出 code={exitCode}，输出排空等待结果={(drained ? "读线程已结束" : "超时 2s，仍发送 exit")}");
#endif
        }

        _dispatch(() => ProcessExited?.Invoke(exitCode));
    }

    /// <summary>
    /// 拆卸顺序（原子防重入）：停止输入队列/写线程 → 终止进程（保底）→
    /// 关闭伪终端 → 等待读/等待线程退出 → 关闭输入管道/Job 句柄
    /// （触发 KILL_ON_JOB_CLOSE）→ 释放属性列表。
    /// 所有句柄关闭均经 TakeHandle 与后台线程互斥。
    /// 留痕（Phase D 评审 11，M0 遗留特性）：本方法在调用线程（通常为 UI 线程）
    /// 同步 Join 三个后台线程，最长约 3s（1+1+1）；后续可将拆卸移后台线程执行，
    /// 避免多标签同时关闭时 UI 线程累积阻塞。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;

        // 1. 停止输入生产与写线程（先停队列再关句柄，避免写线程对新关闭句柄发起 WriteFile）
        var queue = _inputQueue;
        if (queue != null)
        {
            try { queue.CompleteAdding(); } catch (ObjectDisposedException) { }
        }
        _writerThread?.Join(TimeSpan.FromSeconds(1));
        queue?.Dispose();

        // 2. 终止子进程（保底）
        var process = TakeHandle(ref _process);
        if (process != IntPtr.Zero)
        {
            TerminateProcess(process, 0);
            CloseHandle(process);
        }

        // 3. 先关闭仍由宿主持有的 ConPTY 侧管道端。Start 中途失败时这些句柄
        // 尚未转交后的清理路径；若先 ClosePseudoConsole，conhost 可能等待仍开放的
        // 管道端而让异常回滚卡死。
        var conInRead = TakeHandle(ref _conInRead);
        if (conInRead != IntPtr.Zero)
            CloseHandle(conInRead);

        var conOutWrite = TakeHandle(ref _conOutWrite);
        if (conOutWrite != IntPtr.Zero)
            CloseHandle(conOutWrite);

        // 4. 关闭伪终端（conhost 随之退出，输出管道写端关闭，读线程解除阻塞）
        var hPc = TakeHandle(ref _hPC);
        if (hPc != IntPtr.Zero)
            ClosePseudoConsole(hPc);

        // 5. 唤醒可能阻塞在 ReadFile 的读线程并等待读/等待线程退出
        IntPtr outRead;
        lock (_handleLock) { outRead = _conOutRead; } // 只读：关闭归读线程 finally 或下方兜底
        if (outRead != IntPtr.Zero)
            CancelIoEx(outRead, IntPtr.Zero);
        _readerThread?.Join(TimeSpan.FromSeconds(1));
        _waiterThread?.Join(TimeSpan.FromSeconds(1));

        // 6. 兜底关闭输入管道（正常路径句柄已被写线程经 TakeHandle 取走，此处返回 Zero）
        var conIn = TakeHandle(ref _conInWrite);
        if (conIn != IntPtr.Zero)
            CloseHandle(conIn);

        // 7. 最后关闭 Job 句柄：杀死任何残留的进程树成员
        var job = TakeHandle(ref _job);
        if (job != IntPtr.Zero)
            CloseHandle(job);

        // 8. 释放属性列表
        var attrList = _attrList;
        _attrList = IntPtr.Zero;
        if (attrList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    /// <summary>继承当前进程环境，覆盖注入 PYTHONIOENCODING=utf-8 / PYTHONUTF8=1，
    /// 再并入可选的额外环境变量（调用方算好传入，如 venv 的 VIRTUAL_ENV/PATH），
    /// 生成双 \0 结尾的 Unicode 环境块。</summary>
    private static string BuildUnicodeEnvironmentBlock(IReadOnlyDictionary<string, string>? extraEnvironment)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key)
                variables[key] = entry.Value as string ?? string.Empty;
        }

        variables["PYTHONIOENCODING"] = "utf-8";
        variables["PYTHONUTF8"] = "1";

        if (extraEnvironment != null)
        {
            foreach (var pair in extraEnvironment)
                variables[pair.Key] = pair.Value;
        }

        var builder = new StringBuilder();
        foreach (var pair in variables)
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        builder.Append('\0');

        return builder.ToString();
    }
}
