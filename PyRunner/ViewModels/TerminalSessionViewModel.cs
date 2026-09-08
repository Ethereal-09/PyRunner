using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using PyRunner.Services;
using PyRunner.Terminal;

namespace PyRunner.ViewModels;

/// <summary>
/// 终端会话视图模型（Phase D 交付 12）：每个运行标签一个实例，托管 WebView2 ⇄ ConPTY 全链路。
/// 桥接逻辑自 M0 PoC 的 MainViewModel 逐行原样迁移，仅摘除 PocScript 硬编码改为构造参数
/// （CommandLine/WorkingDirectory/ExtraEnvironment/ScriptId/DisplayName）。
/// PostMessage 协议：
///   前端 → 后端：{ type: "input"|"resize"|"ready"|"selection"|"copyRequest"|"pasteRequest", ... }
///   后端 → 前端：{ type: "output"|"clear"|"exit"|"connected"|"requestSelection", ... }
/// </summary>
public partial class TerminalSessionViewModel : ObservableObject
{
    private const string DataDirectoryEnvironmentVariable = "PYRUNNER_DATA_DIRECTORY";
    private const string WebViewDataDirectoryEnvironmentVariable = "WEBVIEW2_USER_DATA_FOLDER";
    private const string VirtualHostName = "pyrunner.local";
    private const short InitialColumns = 80;
    private const short InitialRows = 24;

    private readonly string _commandLine;
    private readonly string? _workingDirectory;
    private readonly IReadOnlyDictionary<string, string>? _extraEnvironment;
    private readonly bool _liveFlush;
    private readonly bool _autoClearOnExit;
    private readonly string? _displayCommand;
    private bool _displayCommandShown;
    private bool _isLightTheme;
    private readonly TailOutputBuffer _deferredOutput = new();
    private TaskCompletionSource<string?>? _copySelectionCompletion;

    /// <summary>所属脚本 Id（运行编排与徽章状态机消费）。</summary>
    public int ScriptId { get; }

    /// <summary>标签显示名（脚本登记名）。</summary>
    public string DisplayName { get; }

    private CoreWebView2? _coreWebView;
    private DispatcherQueue? _dispatcher;
    private PtySession? _session;
    private bool _sessionStarted;

    /// <summary>WebView2 控件引用（Phase D 评审必修 1）：Shutdown 时 Close 以阻断
    /// EnsureCoreWebView2Async 之后的 continuation 继续走到启动 Python。</summary>
    private WebView2? _webView;

    /// <summary>Shutdown 已请求（仅 UI 线程读写）：置位后任何就绪 continuation
    /// 都不得再启动 Python 会话，杜绝「关标签/关窗口落在 WebView2 初始化窗口内 →
    /// ready 后照样启动无人管理的孤儿 python.exe」竞态（必修 1）。</summary>
    private bool _shutdownRequested;

    private readonly ILocalizationService _localization;

    /// <summary>当前状态栏文案的资源键与格式参数：语言热切换时据此重算 Status，
    /// 避免状态栏停留在切换前的旧语言文案（仅 UI 线程读写）。</summary>
    private string _statusKey = "Status_Initializing";
    private object[]? _statusArgs;

    public TerminalSessionViewModel(
        ILocalizationService localization,
        string commandLine,
        int scriptId,
        string displayName,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? extraEnvironment = null,
        bool liveFlush = true,
        bool autoClearOnExit = false,
        string? displayCommand = null)
    {
        _localization = localization;
        _commandLine = commandLine;
        ScriptId = scriptId;
        DisplayName = displayName;
        _workingDirectory = workingDirectory;
        _extraEnvironment = extraEnvironment;
        _liveFlush = liveFlush;
        _autoClearOnExit = autoClearOnExit;
        _displayCommand = displayCommand;
        // 语言热切换：刷新当前状态栏文案；Shutdown 时退订（服务为单例，避免持有瞬态 VM）
        _localization.LanguageChanged += OnLanguageChanged;
        SetLocalizedStatus("Status_Initializing");
    }

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>ConPTY 输出旁路（base64 原始字节）：运行编排累积写 RunRecord 用，
    /// 与转发前端的 PostToHost 互不干扰。</summary>
    public event Action<string>? OutputProduced;

    /// <summary>子进程退出旁路（退出码）：运行编排落库与状态映射用。</summary>
    public event Action<int>? Exited;

    /// <summary>设置状态栏文案（记录资源键，供语言切换重算）。</summary>
    private void SetLocalizedStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args.Length == 0 ? null : args;
        Status = args.Length == 0
            ? _localization[key]
            : string.Format(_localization[key], args);
    }

    private void OnLanguageChanged() =>
        SetLocalizedStatus(_statusKey, _statusArgs ?? Array.Empty<object>());

    /// <summary>Python 进程是否已真实启动（评审修复 3：InitializeTabAsync 失败路径
    /// 据此区分「从未启动 → AbortNotStarted 落库 Failed」与「已启动 → CloseSession 落库 Killed」）。</summary>
    public bool IsSessionStarted => _sessionStarted;

    /// <summary>Shutdown 是否已请求（评审修复 3：初始化失败若由关标签/关窗口触发，
    /// 终结已由 CloseSession 完成，InitializeTabAsync 不再重复落库）。</summary>
    public bool IsShutdownRequested => _shutdownRequested;

    /// <summary>初始化 WebView2、映射本地 wwwroot 并导航到终端页面。</summary>
    public async Task InitializeAsync(WebView2 webView)
    {
#if DEBUG
        DebugLog.WriteLine("VM: InitializeAsync 开始");
#endif
        _webView = webView; // 先记录引用：Shutdown 可在此挂起期间 Close 阻断后续就绪

        // unpackaged 默认会把 WebView2 用户数据写到 exe 同目录，污染便携发布包，且安装到
        // 只读目录后可能初始化失败。与数据库/设置使用同一数据根目录，测试环境也可隔离。
        Environment.SetEnvironmentVariable(
            WebViewDataDirectoryEnvironmentVariable,
            ResolveWebViewUserDataDirectory(),
            EnvironmentVariableTarget.Process);
        await webView.EnsureCoreWebView2Async();

        // 就绪竞态防护（必修 1）：挂起期间用户关标签/关窗口已 Shutdown，
        // 立即退出 continuation，不再映射/导航，更不会走到 StartPythonSession
        if (_shutdownRequested)
        {
#if DEBUG
            DebugLog.WriteLine("VM: InitializeAsync 就绪后检测到 Shutdown，放弃后续初始化");
#endif
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();

        var coreWebView = webView.CoreWebView2;
        _coreWebView = coreWebView;

        coreWebView.Settings.AreDefaultContextMenusEnabled = false;
        coreWebView.Settings.AreDevToolsEnabled = false;
        coreWebView.Settings.IsStatusBarEnabled = false;

        var wwwrootPath = Path.Combine(AppContext.BaseDirectory, "Terminal", "wwwroot");
#if DEBUG
        DebugLog.WriteLine($"VM: CoreWebView2 就绪，映射 wwwroot={wwwrootPath} 存在={Directory.Exists(wwwrootPath)}");
#endif
        coreWebView.SetVirtualHostNameToFolderMapping(
            VirtualHostName, wwwrootPath, CoreWebView2HostResourceAccessKind.Allow);

        coreWebView.WebMessageReceived += OnWebMessageReceived;
        var themeQuery = _isLightTheme ? "light" : "dark";
        coreWebView.Navigate($"https://{VirtualHostName}/index.html?theme={themeQuery}");
#if DEBUG
        DebugLog.WriteLine("VM: 已发起导航 https://" + VirtualHostName + "/index.html");
#endif

        SetLocalizedStatus("Status_FrontendLoading");
    }

    private static string ResolveWebViewUserDataDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        var dataDirectory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PyRunner")
            : Path.GetFullPath(overrideDirectory);
        var userDataDirectory = Path.Combine(dataDirectory, "WebView2");
        Directory.CreateDirectory(userDataDirectory);
        return userDataDirectory;
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var rawJson = args.WebMessageAsJson;
#if DEBUG
            DebugLog.WriteLine($"RX: {Truncate(rawJson, 200)}");
#endif
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;

            switch (typeElement.GetString())
            {
                case "ready":
                    PostTerminalTheme();
                    StartPythonSession();
                    break;

                case "input":
                    if (root.TryGetProperty("data", out var inputData))
                    {
                        var text = inputData.GetString() ?? string.Empty;
#if DEBUG
                        DebugLog.WriteLine($"VM: input → WriteInput len={text.Length}");
#endif
                        _session?.WriteInput(text);
                    }
                    break;

                case "resize":
                    // TryGetInt32 防格式异常；校验 1..500 合理范围后再下发
                    if (root.TryGetProperty("cols", out var cols) &&
                        root.TryGetProperty("rows", out var rows) &&
                        cols.TryGetInt32(out var c) && rows.TryGetInt32(out var r) &&
                        c >= 1 && c <= 500 && r >= 1 && r <= 500)
                    {
#if DEBUG
                        DebugLog.WriteLine($"VM: resize → {c}x{r}");
#endif
                        _session?.Resize((short)c, (short)r);
                    }
                    break;

                case "selection":
                    var selection = root.TryGetProperty("data", out var selectionData)
                        ? selectionData.GetString()
                        : null;
                    _copySelectionCompletion?.TrySetResult(selection);
                    break;

                case "copyRequest":
                    // WebView2 获得焦点后 WinUI KeyboardAccelerator 可能收不到按键，
                    // 由 xterm 在按键发生时同步捕获选区，避免异步往返期间选区丢失。
                    if (root.TryGetProperty("data", out var copyData))
                        CopyTextToClipboard(copyData.GetString());
                    else
                        _ = CopySelectionAsync();
                    break;

                case "pasteRequest":
                    // Ctrl+Shift+V 与鼠标右键共用此入口；剪贴板只在用户动作后读取。
                    _ = PasteFromClipboardAsync();
                    break;
            }
        }
        catch (Exception ex)
        {
            // 统一捕获全部异常（JSON 解析、类型不匹配、会话启动失败等）：
            // WebView2 事件处理器内逃逸异常在 Release 下会直接崩进程
            LogMessageError(ex);
        }
    }

    /// <summary>仅 DEBUG 生效：记录消息处理异常详情（Release 下调用点被编译器移除，方法体本身不引用 DebugLog）。</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void LogMessageError(Exception ex)
    {
#if DEBUG
        DebugLog.WriteLine($"VM: OnWebMessageReceived 异常: {ex}");
#endif
    }

#if DEBUG
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
#endif

    private void StartPythonSession()
    {
        // 必修 1 二道闸：即使 ready 消息穿透到达，Shutdown 后也不得启动进程
        if (_sessionStarted || _shutdownRequested) return;
        _sessionStarted = true;

        // 命令行由运行编排构造时传入（解释器/脚本路径/参数/引号规则），不再在此拼装
#if DEBUG
        DebugLog.WriteLine($"VM: StartPythonSession cmd={_commandLine}");
#endif

        var session = new PtySession(
            _commandLine, InitialColumns, InitialRows,
            action => _dispatcher?.TryEnqueue(() => action()) == true,
            _workingDirectory, _extraEnvironment);
        session.OutputReceived += base64 =>
        {
#if DEBUG
            DebugLog.WriteLine($"VM: OutputReceived base64Len={base64.Length}");
#endif
            OutputProduced?.Invoke(base64);
            if (_liveFlush)
            {
                // ConPTY 启动会先发送清屏和光标复位序列。命令提示必须放在这段初始化之后，
                // 否则它刚写入 xterm 就会被 \x1b[2J 清掉；脚本首批正文到达前再补首行。
                var initializationCompleted = false;
                var initializationOnly = false;
                if (!_displayCommandShown)
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(base64);
                        var text = System.Text.Encoding.UTF8.GetString(bytes);
                        initializationCompleted = text.Contains("\x1b[?25h", StringComparison.Ordinal);
                        initializationOnly = text.Contains("\x1b[?9001h", StringComparison.Ordinal)
                            || text.Contains("\x1b[?1004h", StringComparison.Ordinal)
                            || text.Contains("\x1b[?25l", StringComparison.Ordinal);
                    }
                    catch (FormatException)
                    {
                        // PtySession 只产生合法 base64；损坏块仍按原协议转发。
                    }
                }

                if (!_displayCommandShown && !initializationOnly)
                    PostDisplayCommand();
                PostToHost(new { type = "output", data = base64 });
                if (!_displayCommandShown && initializationCompleted)
                    PostDisplayCommand();
            }
            else
            {
                try
                {
                    var bytes = Convert.FromBase64String(base64);
                    _deferredOutput.Append(bytes, 0, bytes.Length);
                }
                catch (FormatException)
                {
                    // PtySession 只产生合法 base64；防御协议损坏时跳过该块。
                }
            }
        };
        session.ProcessExited += code =>
        {
#if DEBUG
            DebugLog.WriteLine($"VM: ProcessExited code={code}");
#endif
            if (!_liveFlush)
            {
                var deferred = _deferredOutput.GetText();
                if (deferred.Length > 0)
                {
                    var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(deferred));
                    PostToHost(new { type = "output", data = encoded });
                }
            }
            if (_autoClearOnExit)
                PostToHost(new { type = "clear" });
            PostToHost(new
            {
                type = "exit",
                code,
                message = string.Format(_localization["Terminal_ProcessExited"], code),
            });
            SetLocalizedStatus("Status_ProcessExited", code);
            Exited?.Invoke(code);
        };

        // 先挂到字段再 Start：Start 失败时 Shutdown/异常路径能统一经 Dispose 回滚资源
        _session = session;
        try
        {
            session.Start();
        }
        catch (Exception ex)
        {
#if DEBUG
            // Release 下 ex 仅用于隔离异常，详细信息只写 Debug 日志
            DebugLog.WriteLine($"VM: Python 启动失败: {ex}");
#endif
            _session = null;
            session.Dispose();
            SetLocalizedStatus("Status_PythonStartFailed", ex.Message);
            StartFailed?.Invoke(ex.Message);
            return;
        }

        PostToHost(new { type = "connected" });
        SetLocalizedStatus("Status_Connected");
    }

    /// <summary>向 xterm 写入设计稿首行命令；仅显示一次且不进入脚本原始输出记录。</summary>
    private void PostDisplayCommand()
    {
        if (_displayCommandShown || string.IsNullOrWhiteSpace(_displayCommand)) return;
        _displayCommandShown = true;

        const string prefix = "> python";
        var suffix = _displayCommand.StartsWith(prefix, StringComparison.Ordinal)
            ? _displayCommand[prefix.Length..]
            : " " + _displayCommand;
        var promptColor = _isLightTheme ? "47;158;112" : "103;206;160";
        var commandColor = _isLightTheme ? "36;94;145" : "134;183;232";
        var prompt = $"\x1b[38;2;{promptColor}m>\x1b[0m \x1b[38;2;{commandColor}mpython\x1b[0m{suffix}\r\n";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(prompt));
        PostToHost(new { type = "output", data = encoded });
    }

    /// <summary>Python 进程启动失败旁路（运行编排据此落库 Failed 并回收标签）。</summary>
    public event Action<string>? StartFailed;

    /// <summary>停止编排第一步：向 ConPTY 写入 ETX（\x03），触发 Python KeyboardInterrupt（spike 实证可达）。</summary>
    public void RequestInterrupt() => _session?.WriteInput("\x03");

    /// <summary>快捷键 Ctrl+Shift+V（Phase E）：剪贴板文本写入 ConPTY stdin。
    /// 换行统一转 CR（终端语义：CR=提交一行），空剪贴板/会话未启动均无操作。
    /// wwwroot 冻结无前端粘贴钩子，故走后端 stdin 直写（与键入同路径）。</summary>
    public void PasteClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var normalized = text.Replace("\r\n", "\n").Replace('\n', '\r');
        _session?.WriteInput(normalized);
    }

    /// <summary>读取系统剪贴板并粘贴到本会话；快捷键与终端右键共用。</summary>
    public async Task PasteFromClipboardAsync()
    {
        try
        {
            var package = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
            PasteClipboard(await package.GetTextAsync());
        }
        catch
        {
            // 剪贴板可能被其他进程短暂占用；终端主链路不得因此失败。
        }
    }

    /// <summary>热切换 xterm 配色；仅发送主题名，不导航页面、不影响缓冲区和 Python 进程。</summary>
    public void ApplyTheme(bool isLight)
    {
        _isLightTheme = isLight;
        if (_coreWebView != null)
            PostTerminalTheme();
    }

    private void PostTerminalTheme() =>
        PostToHost(new { type = "theme", theme = _isLightTheme ? "light" : "dark" });

    /// <summary>快捷键 Ctrl+Shift+C：经 PostMessage 请求 xterm.js 的真实终端选区。</summary>
    public async Task CopySelectionAsync()
    {
        if (_coreWebView == null) return;

        try
        {
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _copySelectionCompletion?.TrySetCanceled();
            _copySelectionCompletion = completion;
            PostToHost(new { type = "requestSelection" });

            var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(1)));
            if (finished != completion.Task) return;
            var text = await completion.Task;
            CopyTextToClipboard(text);
        }
        catch
        {
            // 页面响应/剪贴板故障不影响主链路
        }
        finally
        {
            _copySelectionCompletion = null;
        }
    }

    private static void CopyTextToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        Windows.ApplicationModel.DataTransfer.DataPackage package = new()
        {
            RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy,
        };
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    /// <summary>停止编排兜底：\x03 超时后强制拆卸会话（Job Object 连带杀死进程树）。</summary>
    public void ForceKill()
    {
        var session = _session;
        _session = null;
        session?.Dispose();
    }

    /// <summary>序列化 JSON 并经 PostWebMessageAsString 发给前端（必须 UI 线程）。</summary>
    private void PostToHost(object payload)
    {
        var coreWebView = _coreWebView;
        if (coreWebView == null)
        {
#if DEBUG
            DebugLog.WriteLine("VM: PostToHost 时 CoreWebView2 为空，丢弃消息");
#endif
            return;
        }

        var json = JsonSerializer.Serialize(payload);
        var dispatcher = _dispatcher;

        if (dispatcher == null || dispatcher.HasThreadAccess)
        {
#if DEBUG
            DebugLog.WriteLine($"TX(直发): {Truncate(json, 120)}");
#endif
            PostWebMessageSafe(coreWebView, json);
        }
        else
        {
#if DEBUG
            DebugLog.WriteLine($"TX(入队): {Truncate(json, 120)}");
#endif
            dispatcher.TryEnqueue(() => PostWebMessageSafe(coreWebView, json));
        }
    }

    private static void PostWebMessageSafe(CoreWebView2 coreWebView, string json)
    {
        try
        {
            coreWebView.PostWebMessageAsString(json);
        }
#if DEBUG
        catch (Exception ex)
        {
            DebugLog.WriteLine($"VM: PostWebMessageAsString 异常: {ex}");
        }
#else
        catch
        {
            // 页面可能已卸载，忽略
        }
#endif
    }

    /// <summary>关闭标签/窗口时释放 ConPTY 会话（Job Object 兜底杀掉整棵进程树）。
    /// 必修 1：置 _shutdownRequested 并 Close WebView2，阻断仍在挂起的
    /// EnsureCoreWebView2Async continuation 于就绪后启动孤儿 Python。</summary>
    public void Shutdown()
    {
        if (_shutdownRequested) return;
        _shutdownRequested = true;
        _copySelectionCompletion?.TrySetCanceled();
        _copySelectionCompletion = null;

        _localization.LanguageChanged -= OnLanguageChanged;
        var session = _session;
        _session = null;
        session?.Dispose();

        var webView = _webView;
        _webView = null;
        try
        {
            webView?.Close(); // 释放 WebView2 并令后续就绪/导航不再有效
        }
        catch
        {
            // 控件可能已随标签移除卸载，忽略
        }
        SetLocalizedStatus("Status_Exited");
    }
}
