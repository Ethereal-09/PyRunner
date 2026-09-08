using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PyRunner.Helpers;
using PyRunner.Models;
using PyRunner.Services;
using PyRunner.ViewModels;
using PyRunner.Views;
using System.Diagnostics;

namespace PyRunner;

/// <summary>
/// 主窗口（Phase D）：自绘标题栏 | 侧栏（脚本列表+文件树）| 多标签终端工作区 | 状态栏。
/// 每个运行标签懒创建独立 WebView2，承载 <see cref="TerminalSessionViewModel"/>（每标签一个实例），
/// 关闭标签即 Dispose；运行编排（解释器解析/并发互斥/停止/落库）归 <see cref="RunCoordinator"/>。
/// 依赖全部强类型注入（由 App 从容器解析传入），不再使用 IServiceProvider locator。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>设计稿布局版本：v2 将旧版本持久化的宽侧栏迁回原型规定的 290 DIP。</summary>
    private const int CurrentUiLayoutVersion = 2;

    /// <summary>侧栏宽度合法范围（DIP）：拖拽写回 AppSettings.SidebarWidth 的钳制口径。</summary>
    private const int SidebarWidthMin = 180;
    private const int SidebarWidthMax = 500;

    private bool _sizeApplied;
    private bool _startupUpdateScheduled;
    private bool _isLightTheme;
    private RunStatus? _lastRunStatus;
    private DateTime? _lastRunTime;

    // Ctrl+, 实现（最终方案）：全局低级键盘钩子 WH_KEYBOARD_LL。背景：OEM 标点键在
    // 本工具链三条路径均不可用（加速器/PreviewKeyDown/窗口子类化，见构造函数沿革注释）；
    // LL 钩子在 IME 处理前看到原始 VK，仅本窗在前台时响应并吞掉逗号键。
    // 部署边界留痕（Phase E 评审）：UI 线程长阻塞（秒级）时 25H2 可能静默摘除 LL 钩子
    // （已观察案例），Ctrl+, 当次会话失效且阻塞结束后不恢复；不做自愈——
    // 快捷键非关键路径（标题栏按钮仍可打开设置）。
    private IntPtr _kbHook = IntPtr.Zero;
    private LowLevelKeyboardProc? _kbHookDelegate; // 防委托回收
    private delegate IntPtr LowLevelKeyboardProc(int nCode, nuint wParam, nint lParam);
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN_LL = 0x0100;
    private const int VK_CONTROL = 0x11;
    private const int VK_OEM_COMMA = 0xBC;
    private const int VK_LCONTROL = 0xA2; // LL 钩子上报的左 Ctrl（物理/注入按键不带 VK_CONTROL 泛码，评审修 3 补丁）
    private const int VK_RCONTROL = 0xA3;
    private const int LLKHF_UP = 0x80; // KBDLLHOOKSTRUCT.flags 位：键已释放（评审修 3）

    /// <summary>Ctrl+, 去重闩（评审修 3）：按住逗号时自动重复的 KeyDown 不得连续打开设置；
    /// 触发一次后置位，观察到 Ctrl 释放（KeyUp）后复位。仅钩子回调内读写。</summary>
    private bool _settingsHotkeyLatched;
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, nuint wParam, nint lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>ContentDialog 在途互斥标志（仅 UI 线程读写）：WinUI 3 同 XamlRoot 上第二个
    /// ShowAsync 会抛 InvalidOperationException；新增/编辑/删除/设置/运行引导入口共用，
    /// 在途直接 return，杜绝异常被 async void 兜底静默吞掉。
    /// 契约（评审修复）：各入口「检查通过后立即置位，delay 也置于标志保护内，finally 复位」，
    /// 杜绝 200ms 等待窗口内被无延迟入口并发穿透（TOCTOU）；
    /// Show*DialogAsync 内部不再自行置位/复位，标志生命周期归入口所有。</summary>
    private bool _dialogInFlight;
    private bool _isClosing;

    // ---- 侧栏拖拽调宽状态（仅 UI 线程） ----
    private bool _thumbDragging;
    private double _dragStartX;
    private double _dragStartWidth;

    /// <summary>标签 ↔ 会话 VM 映射（仅 UI 线程）；关闭标签即 Dispose 会话。</summary>
    private readonly Dictionary<TabViewItem, TerminalSessionViewModel> _tabSessions = new();

    /// <summary>标签 VM 状态栏联动订阅记录（关标签时具名退订）。</summary>
    private readonly Dictionary<TabViewItem, System.ComponentModel.PropertyChangedEventHandler> _tabStatusHandlers = new();

    private readonly IScriptService _scriptService;
    private readonly IScriptPathService _scriptPathService;
    private readonly ISettingsService _settingsService;
    private readonly ILocalizationService _localization;
    private readonly ScriptListViewModel _scriptListViewModel;
    private readonly FileTreeViewModel _fileTreeViewModel;
    private readonly RunCoordinator _coordinator;
    private readonly Func<ScriptEditDialog> _scriptEditDialogFactory;
    private readonly Func<SettingsDialog> _settingsDialogFactory;
    private readonly INotificationService _notifications;
    private readonly IScriptDirectoryWatcher _watcher;
    private readonly RunHistoryViewModel _historyViewModel;
    private readonly IShellNavigationService _navigation;
    private readonly UpdateCheckCoordinator _updateCoordinator;
    private readonly CancellationTokenSource _startupUpdateCancellation = new();
    private Task _startupUpdateTask = Task.CompletedTask;
    private readonly HelpAboutPage _helpPage;

    public MainViewModel ViewModel { get; }

    /// <summary>标题栏应用名（x:Bind；语言切换经 Bindings.Update 刷新）。</summary>
    public string AppName => _localization["App_Name"];

    /// <summary>标题栏主题按钮：显示要切换到的目标主题。</summary>
    public string ThemeToggleText => _localization[_isLightTheme ? "Theme_SwitchToDark" : "Theme_SwitchToLight"];
    public string ThemeToggleGlyph => _isLightTheme ? "\uE708" : "\uE706";

    public string NavScriptsText => _localization["Nav_Scripts"];
    public string NavRunsText => _localization["Nav_Runs"];
    public string NavSettingsText => _localization["Nav_Settings"];
    public string NavHelpText => _localization["Nav_Help"];
    public string EditText => _localization["Button_Edit"];
    public string NoScriptSelectedText => _localization["Script_NoneSelected"];
    public string SelectScriptHintText => _localization["Script_SelectHint"];
    public string NotRunText => _localization["StatusBadge_NotRun"];
    public string RunsTitleText => _localization["History_Title"];
    public string TerminalCommandPlaceholderText => _localization["Terminal_CommandPlaceholder"];
    public string StatusNoRunText => _localization["Status_NoRun"];
    public string StatusNoDirectoryText => _localization["Status_NoDirectory"];

    /// <summary>当前选中的脚本（壳级占位；数据源为侧栏 VM，运行协调入口消费）。</summary>
    public Script? SelectedScript => _scriptListViewModel.SelectedScript;

    public MainWindow(
        IScriptService scriptService,
        IScriptPathService scriptPathService,
        ISettingsService settingsService,
        ILocalizationService localization,
        MainViewModel viewModel,
        ScriptListViewModel scriptListViewModel,
        FileTreeViewModel fileTreeViewModel,
        RunCoordinator coordinator,
        Func<ScriptEditDialog> scriptEditDialogFactory,
        Func<SettingsDialog> settingsDialogFactory,
        INotificationService notifications,
        IScriptDirectoryWatcher watcher,
        RunHistoryViewModel historyViewModel,
        IShellNavigationService navigation,
        Func<HelpAboutPage> helpAboutPageFactory,
        UpdateCheckCoordinator updateCoordinator)
    {
        _scriptService = scriptService;
        _scriptPathService = scriptPathService;
        _settingsService = settingsService;
        _localization = localization;
        _scriptListViewModel = scriptListViewModel;
        _fileTreeViewModel = fileTreeViewModel;
        _coordinator = coordinator;
        _scriptEditDialogFactory = scriptEditDialogFactory;
        _settingsDialogFactory = settingsDialogFactory;
        _notifications = notifications;
        _watcher = watcher;
        _historyViewModel = historyViewModel;
        _navigation = navigation;
        _updateCoordinator = updateCoordinator;
        ViewModel = viewModel;

        // Mica 系统背景（Phase C 交付 7）：Win11 呈现云母材质，Win10 自动回退纯色
        SystemBackdrop = new MicaBackdrop();

        InitializeComponent();

        _helpPage = helpAboutPageFactory();
        _helpPage.SettingsRequested += OnHelpSettingsRequested;
        HelpPageHost.Content = _helpPage;
        _navigation.Navigated += OnShellNavigated;

        // 主题设置在首帧建立后立即应用；默认 Dark 保持升级前视觉。
        ApplyTheme(string.Equals(_settingsService.Current.Theme, "Light", StringComparison.OrdinalIgnoreCase), persist: false);

        // Ctrl+, 设置快捷键（补充文档 §五）实现沿革：
        //  1) XAML 声明 Key="OemComma" → XamlCompiler 静默退出码 1（WAS 1.5.250108004 工具链缺陷）；
        //  2) 代码创建 KeyboardAccelerator{Key=(VirtualKey)188} 构造期挂树 → 首帧合成期原生
        //     failfast 0xc000027b（Windows 25H2 + WAS 3.1.5 实测）；Loaded 后注册不崩溃，
        //     但 OEM 标点键加速器实测从不触发（注册 count=1 仍无响应）；
        //  3) 根 Grid PreviewKeyDown 拦截 → 中文 IME 布局下逗号 KeyDown 被输入法层吞掉，
        //     只收到修饰键（实测日志仅 key=17）；
        //  4) InputSite 子类化钩 WM_CHAR → WAS 1.5 无 InputSite Win32 子窗（键盘走
        //     CoreMessaging/TSF，实测枚举子窗仅 3 个且无 InputSite）；
        //  5) 最终方案：WH_KEYBOARD_LL 全局低级钩子，仅本窗前台时响应并吞逗号键
        //     （安装时机见构造函数末尾，评审修 7）

        // index.html 原型规定侧栏 290 DIP。旧测试版本曾把 420 DIP 写进用户设置；
        // 布局版本只迁移一次，之后仍允许用户拖拽并持久化自己的宽度。
        if (_settingsService.Current.UiLayoutVersion < CurrentUiLayoutVersion)
        {
            _settingsService.Update(settings =>
            {
                settings.SidebarWidth = 290;
                settings.UiLayoutVersion = CurrentUiLayoutVersion;
            });
        }

        // 侧栏宽度单一真相源：AppSettings.SidebarWidth（XAML 不再硬编码），
        // 钳制 180~500；拖拽结束写回同一键（见 OnThumbPointerReleased）
        SidebarColumn.Width = new GridLength(Math.Clamp(_settingsService.Current.SidebarWidth, SidebarWidthMin, SidebarWidthMax));

        // 自绘标题栏：内容延伸到标题区域，仅中间空白 TitleBarDragRegion 提供系统拖动；
        // 导航和主题按钮留在拖动区之外，确保真实指针输入不会被非客户区吞掉。
        // 系统 caption 按钮（最小化/最大化/关闭）保留原生语义覆盖右上角
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);

        // 窗口标题走资源键；语言热切换时同步刷新
        Title = _localization["Window_Title"];
        var appIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(appIconPath))
            AppWindow.SetIcon(appIconPath);
        _localization.LanguageChanged += OnLanguageChanged;

        Closed += OnClosed;
        Activated += OnActivated;

        // 侧栏：脚本列表 + 文件树双 VM 注入；脚本由配置目录自动导入，保留编辑/删除/运行入口
        Sidebar.Initialize(_scriptListViewModel, _fileTreeViewModel);
        Sidebar.EditRequested += OnSidebarEdit;
        Sidebar.DeleteRequested += OnSidebarDelete;
        Sidebar.RunRequested += OnSidebarRun;
        // Phase E 新增入口：记事本 / 收藏切换 / 手动刷新 / 空状态引导去设置
        Sidebar.NotebookRequested += OnSidebarNotebook;
        Sidebar.FavoriteToggleRequested += OnSidebarFavoriteToggle;
        Sidebar.RefreshRequested += OnSidebarRefresh;
        Sidebar.OpenSettingsRequested += OnSidebarOpenSettings;
        Sidebar.OpenInterpreterSettingsRequested += OnSidebarOpenInterpreterSettings;

        // 文件树 .py 点击：已登记 → 选中；未登记（监听竞态）→ 自动同步目录后选中。
        _fileTreeViewModel.RegisteredScriptClicked += OnTreeRegisteredScript;
        _fileTreeViewModel.UnregisteredScriptClicked += OnTreeUnregisteredScript;
        _fileTreeViewModel.ScriptRunRequested += OnTreeRunRequested;
        _fileTreeViewModel.ScriptNotebookRequested += OnTreeNotebookRequested;
        _fileTreeViewModel.FavoriteToggleRequested += OnTreeFavoriteToggleRequested;

        // 运行编排状态广播：侧栏徽章 + 运行/停止按钮状态机
        _coordinator.StatusChanged += OnRunStatusChanged;

        // 列表选中变化 → 壳 VM 运行状态机（具名订阅，OnClosed 退订）
        _scriptListViewModel.PropertyChanged += OnListSelectionChanged;

        // 运行历史面板（Phase E）：DataContext 注入，选中/终结时刷新
        HistoryPanel.DataContext = _historyViewModel;
        _historyViewModel.Load(null);

        // 目录监听（Phase E）：去抖广播在线程池，封送 UI 线程后重建树与列表
        if (_settingsService.Current.AutoRefreshScripts)
        {
            _watcher.Changed += OnWatcherChanged;
            _watcher.SyncFromStore();
        }

        // WinUI 3 的 Window 没有 Loaded 事件；多标签工作区懒创建，启动不建任何 WebView2。
        // 列表/文件树加载即刻执行
        _scriptListViewModel.Load();
        _fileTreeViewModel.Load();
        RefreshSelectedScriptPresentation();
        RefreshLocalizedToolTips();
        _navigation.Navigate(ShellPage.Scripts);
        ViewModel.SetLocalizedStatus("Status_Ready");

        if (_settingsService.Current.AutoRefreshScripts)
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => _ = ImportConfiguredScriptsOnStartupAsync());
        }

        // LL 钩子安装时机（评审修 7）：移到构造函数最末（全部接线与 Closed 订阅之后）——
        // 构造中途失败路径不再滞留系统级钩子；卸载已在 OnClosed（上方已订阅）
        _kbHookDelegate = LowLevelKeyboardCallback;
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbHookDelegate, IntPtr.Zero, 0);
#if DEBUG
        DebugLog.WriteLine($"Window: WH_KEYBOARD_LL installed hhk={_kbHook}");
#endif
    }

    /// <summary>低级键盘钩子：本窗在前台且 Ctrl 按下时拦截逗号 KeyDown → 打开设置，
    /// 返回 1 吞掉该键（否则逗号仍会经 IME/CoreMessaging 进入焦点控件）；
    /// 其余情况透传。回调在钩子线程触发，UI 操作封送 DispatcherQueue。
    /// 评审修 3：读 KBDLLHOOKSTRUCT.flags（lParam+8）过滤 KeyUp/注入事件，
    /// 按住逗号时自动重复的 KeyDown 经去重闩吞掉，Ctrl 释放后复位。</summary>
    private IntPtr LowLevelKeyboardCallback(int nCode, nuint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var vk = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
            var flags = System.Runtime.InteropServices.Marshal.ReadInt32(lParam + 8); // KBDLLHOOKSTRUCT.flags 偏移
            var isKeyUp = (flags & LLKHF_UP) != 0;

            // Ctrl 释放 → 复位去重闩（下次 Ctrl+, 重新生效）。
            // 注：LL 钩子收到的是 VK_LCONTROL/VK_RCONTROL 具体码（实测无 VK_CONTROL 泛码），三者均须复位
            if ((vk == VK_CONTROL || vk == VK_LCONTROL || vk == VK_RCONTROL) && isKeyUp)
                _settingsHotkeyLatched = false;

            if (!isKeyUp && wParam == WM_KEYDOWN_LL && vk == VK_OEM_COMMA
                && (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0
                && GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this))
            {
#if DEBUG
                DebugLog.WriteLine($"Window: Ctrl+, 触发 latch={_settingsHotkeyLatched} dialogInFlight={_dialogInFlight}");
#endif
                if (_settingsHotkeyLatched)
                    return (IntPtr)1; // 按住自动重复：吞键但不重复打开设置（评审修 3）
                _settingsHotkeyLatched = true;
                DispatcherQueue.TryEnqueue(() => _ = OpenSettingsGuardedAsync(null));
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_sizeApplied) return;
        _sizeApplied = true;
        // 设计图是 2560x1440 桌面上的最大化窗口；用系统 presenter 适配任意工作区，
        // 避免把 DIP 再乘缩放率后传给像素 API 导致窗口尺寸和布局比例失真。
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Maximize();
#if DEBUG
        DebugLog.WriteLine("Window: 首次激活，按设计稿最大化");
#endif
        if (!_startupUpdateScheduled)
        {
            _startupUpdateScheduled = true;
            _startupUpdateTask = RunDelayedAutomaticUpdateCheckAsync();
        }
    }

    /// <summary>首帧显示后延迟7秒执行后台检查；不弹窗，不阻塞窗口激活。</summary>
    private async Task RunDelayedAutomaticUpdateCheckAsync()
    {
        try
        {
            await _updateCoordinator.ScheduleAutomaticCheckAsync(
                TimeSpan.FromSeconds(7),
                _startupUpdateCancellation.Token);
        }
        catch (OperationCanceledException) when (_startupUpdateCancellation.IsCancellationRequested)
        {
            // 窗口关闭：丢弃尚未开始或正在等待的启动检查。
        }
        catch (ObjectDisposedException) when (_isClosing)
        {
            // 窗口关闭期间依赖容器正在释放，正常忽略。
        }
        catch (Exception ex)
        {
            _ = ex;
#if DEBUG
            DebugLog.WriteLine($"Update: automatic check failed ({ex.Message})");
#endif
        }
    }

    private void OnLanguageChanged()
    {
        Title = _localization["Window_Title"];
        Bindings.Update();
        RefreshLocalizedToolTips();
        RefreshSelectedScriptPresentation();
        RefreshTerminalTabHeaders();
    }

    private void RefreshLocalizedToolTips()
    {
        ToolTipService.SetToolTip(RunButton, $"{ViewModel.RunText} (F5)");
        ToolTipService.SetToolTip(StopButton, $"{ViewModel.StopText} (Shift+F5)");
        ToolTipService.SetToolTip(EditButton, $"{EditText} (Ctrl+E)");
    }

    private void OnThemeToggleClick(object sender, RoutedEventArgs e) => ApplyTheme(!_isLightTheme, persist: true);

    private void ApplyTheme(bool isLight, bool persist)
    {
        _isLightTheme = isLight;
        var requestedTheme = isLight ? ElementTheme.Light : ElementTheme.Dark;
        Views.ThemeBrushes.SetTheme(requestedTheme);
        RootLayout.RequestedTheme = requestedTheme;

        // 自绘标题区右侧仍使用系统 caption 按钮，显式同步前景/悬停色。
        AppWindow.TitleBar.ButtonForegroundColor = isLight
            ? Windows.UI.Color.FromArgb(0xFF, 0x17, 0x21, 0x2A)
            : Windows.UI.Color.FromArgb(0xFF, 0xD6, 0xDA, 0xE0);
        AppWindow.TitleBar.ButtonHoverBackgroundColor = isLight
            ? Windows.UI.Color.FromArgb(0x17, 0x17, 0x21, 0x2A)
            : Windows.UI.Color.FromArgb(0x21, 0xFF, 0xFF, 0xFF);

        foreach (var session in _tabSessions.Values)
            session.ApplyTheme(isLight);

        if (persist)
        {
            _settingsService.Update(settings => settings.Theme = isLight ? "Light" : "Dark");

            // 转换器创建的 Brush 不是 ThemeResource 表达式，重建可见项以刷新它们。
            var selectedPath = _scriptListViewModel.SelectedScript?.FilePath;
            _scriptListViewModel.Load(selectedPath);
            _fileTreeViewModel.Load();
            RefreshSelectedScriptPresentation();
            if (HelpPageHost.Visibility == Visibility.Visible)
                ShowHelpPage();
            else if (RunsPage.Visibility == Visibility.Visible)
                ShowRunsPage();
            else
                ShowScriptsPage();
        }

        Bindings.Update();
        RefreshButtonRestingVisuals();
    }

    /// <summary>ContentDialog 使用独立 Popup 视觉树，必须显式传递当前窗口主题。</summary>
    private void PrepareDialog(ContentDialog dialog) =>
        DialogHostHelper.Prepare(
            dialog,
            Content.XamlRoot,
            _isLightTheme ? ElementTheme.Light : ElementTheme.Dark);

    // ==== 顶部导航与脚本信息栏 ====

    private void OnNavScriptsClick(object sender, RoutedEventArgs e) => _navigation.Navigate(ShellPage.Scripts);

    private void OnNavRunsClick(object sender, RoutedEventArgs e) => _navigation.Navigate(ShellPage.Runs);

    private void OnNavHelpClick(object sender, RoutedEventArgs e) => _navigation.Navigate(ShellPage.Help);

    private void OnShellNavigated(object? sender, ShellPage page)
    {
        switch (page)
        {
            case ShellPage.Runs:
                ShowRunsPage();
                break;
            case ShellPage.Help:
                ShowHelpPage();
                break;
            default:
                ShowScriptsPage();
                break;
        }
    }

    // 仅补足既有按钮的交互视觉；不改变命令、可用性或操作流程。
    private void OnNavButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button && !IsActiveNavigation(button))
            ApplyButtonChrome(button, Views.ThemeBrushes.Get("GhostButtonHoverBrush", 0xFF30363A));
    }

    private void OnNavButtonPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
            ApplyButtonChrome(button, Views.ThemeBrushes.Get("SecondaryButtonPressedBrush", 0xFF202428));
    }

    private void OnNavButtonPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
            ApplyButtonChrome(button, IsActiveNavigation(button)
                ? Views.ThemeBrushes.Get("NavigationSelectedBrush", 0xFF30363A)
                : Views.ThemeBrushes.Get("GhostButtonHoverBrush", 0xFF30363A));
    }

    private void OnNavButtonPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Button button) return;
        ApplyButtonChrome(button, IsActiveNavigation(button)
            ? Views.ThemeBrushes.Get("NavigationSelectedBrush", 0xFF30363A)
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)));
    }

    private bool IsActiveNavigation(Button button) =>
        (button == NavScriptsButton && WorkArea.Visibility == Visibility.Visible && ScriptsPage.Visibility == Visibility.Visible) ||
        (button == NavRunsButton && WorkArea.Visibility == Visibility.Visible && RunsPage.Visibility == Visibility.Visible) ||
        (button == NavHelpButton && HelpPageHost.Visibility == Visibility.Visible);

    private void RefreshButtonRestingVisuals()
    {
        var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        ApplyButtonVisual(
            NavScriptsButton,
            WorkArea.Visibility == Visibility.Visible && ScriptsPage.Visibility == Visibility.Visible
                ? Views.ThemeBrushes.Get("NavigationSelectedBrush", 0xFF30363A)
                : transparent,
            WorkArea.Visibility == Visibility.Visible && ScriptsPage.Visibility == Visibility.Visible
                ? Views.ThemeBrushes.Get("TextPrimaryBrush", 0xFFF0F2F4)
                : Views.ThemeBrushes.Get("TextSecondaryBrush", 0xFFAAB2BC));
        ApplyButtonVisual(
            NavRunsButton,
            WorkArea.Visibility == Visibility.Visible && RunsPage.Visibility == Visibility.Visible
                ? Views.ThemeBrushes.Get("NavigationSelectedBrush", 0xFF30363A)
                : transparent,
            WorkArea.Visibility == Visibility.Visible && RunsPage.Visibility == Visibility.Visible
                ? Views.ThemeBrushes.Get("TextPrimaryBrush", 0xFFF0F2F4)
                : Views.ThemeBrushes.Get("TextSecondaryBrush", 0xFFAAB2BC));
        ApplyButtonVisual(SettingsButton, transparent, Views.ThemeBrushes.Get("TextSecondaryBrush", 0xFFAAB2BC));
        ApplyButtonVisual(
            NavHelpButton,
            HelpPageHost.Visibility == Visibility.Visible
                ? Views.ThemeBrushes.Get("NavigationSelectedBrush", 0xFF30363A)
                : transparent,
            HelpPageHost.Visibility == Visibility.Visible
                ? Views.ThemeBrushes.Get("TextPrimaryBrush", 0xFFF0F2F4)
                : Views.ThemeBrushes.Get("TextSecondaryBrush", 0xFFAAB2BC));
        ApplyButtonVisual(ThemeButton, transparent, Views.ThemeBrushes.Get("TextPrimaryBrush", 0xFFF0F2F4));
        ApplyButtonVisual(
            RunButton,
            Views.ThemeBrushes.Get("AccentBrush", 0xFF35A875),
            RunButton.IsEnabled
                ? Views.ThemeBrushes.Get("PrimaryButtonForegroundBrush", 0xFFFFFFFF)
                : Views.ThemeBrushes.Get("DisabledButtonForegroundBrush", 0xFF7E8995));
        ApplyButtonVisual(
            StopButton,
            StopButton.IsEnabled
                ? Views.ThemeBrushes.Get("DangerSurfaceBrush", 0x2EFF8F8F)
                : Views.ThemeBrushes.Get("DisabledButtonBrush", 0xFF24282C),
            StopButton.IsEnabled
                ? Views.ThemeBrushes.Get("StatusErrorBrush", 0xFFFF8F8F)
                : Views.ThemeBrushes.Get("DisabledButtonForegroundBrush", 0xFF7E8995));

        ApplyButtonVisual(
            EditButton,
            EditButton.IsEnabled
                ? Views.ThemeBrushes.Get("SecondaryButtonBrush", 0xFF2B2F33)
                : Views.ThemeBrushes.Get("DisabledButtonBrush", 0xFF24282C),
            EditButton.IsEnabled
                ? Views.ThemeBrushes.Get("SecondaryButtonForegroundBrush", 0xFFF0F2F4)
                : Views.ThemeBrushes.Get("DisabledButtonForegroundBrush", 0xFF7E8995));
    }

    private void OnThemeButtonPointerEntered(object sender, PointerRoutedEventArgs e) =>
        ApplyButtonChrome(ThemeButton, Views.ThemeBrushes.Get("GhostButtonHoverBrush", 0xFF30363A));

    private void OnThemeButtonPointerPressed(object sender, PointerRoutedEventArgs e) =>
        ApplyButtonChrome(ThemeButton, Views.ThemeBrushes.Get("SecondaryButtonPressedBrush", 0xFF202428));

    private void OnThemeButtonPointerReleased(object sender, PointerRoutedEventArgs e) =>
        ApplyButtonChrome(ThemeButton, Views.ThemeBrushes.Get("GhostButtonHoverBrush", 0xFF30363A));

    private void OnThemeButtonPointerExited(object sender, PointerRoutedEventArgs e) =>
        ApplyButtonChrome(ThemeButton, new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)));

    private void OnRunButtonPointerEntered(object sender, PointerRoutedEventArgs e) =>
        ApplyButtonChrome(RunButton, Views.ThemeBrushes.Get("AccentHoverBrush", 0xFF3DB982));

    private void OnRunButtonPointerPressed(object sender, PointerRoutedEventArgs e) =>
        ApplyButtonChrome(RunButton, Views.ThemeBrushes.Get("AccentPressedBrush", 0xFF2B8E63));

    private void OnRunButtonPointerReleased(object sender, PointerRoutedEventArgs e) =>
        ApplyButtonChrome(RunButton, Views.ThemeBrushes.Get("AccentHoverBrush", 0xFF3DB982));

    private void OnRunButtonPointerExited(object sender, PointerRoutedEventArgs e) =>
        ApplyButtonChrome(RunButton, Views.ThemeBrushes.Get("AccentBrush", 0xFF35A875));

    private void OnStopButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (StopButton.IsEnabled)
            ApplyButtonChrome(StopButton, Views.ThemeBrushes.Get("DangerHoverSurfaceBrush", 0x42FF8F8F));
    }

    private void OnStopButtonPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (StopButton.IsEnabled)
            ApplyButtonChrome(StopButton, Views.ThemeBrushes.Get("DangerPressedSurfaceBrush", 0x5CFF8F8F));
    }

    private void OnStopButtonPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (StopButton.IsEnabled)
            ApplyButtonChrome(StopButton, Views.ThemeBrushes.Get("DangerHoverSurfaceBrush", 0x42FF8F8F));
    }

    private void OnStopButtonPointerExited(object sender, PointerRoutedEventArgs e) =>
        ApplyButtonChrome(StopButton, StopButton.IsEnabled
            ? Views.ThemeBrushes.Get("DangerSurfaceBrush", 0x2EFF8F8F)
            : Views.ThemeBrushes.Get("DisabledButtonBrush", 0xFF24282C));

    private void OnSecondaryButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button { IsEnabled: true } button)
            ApplyButtonChrome(button, Views.ThemeBrushes.Get("SecondaryButtonHoverBrush", 0xFF343A3F));
    }

    private void OnSecondaryButtonPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button { IsEnabled: true } button)
            ApplyButtonChrome(button, Views.ThemeBrushes.Get("SecondaryButtonPressedBrush", 0xFF202428));
    }

    private void OnSecondaryButtonPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button { IsEnabled: true } button)
            ApplyButtonChrome(button, Views.ThemeBrushes.Get("SecondaryButtonHoverBrush", 0xFF343A3F));
    }

    private void OnSecondaryButtonPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
            ApplyButtonChrome(button, Views.ThemeBrushes.Get("SecondaryButtonBrush", 0xFF2B2F33));
    }

    private void ApplyButtonChrome(Button button, Brush brush)
    {
        button.Background = brush;
        // 默认模板在 PointerOver/Pressed 状态会直接写 ContentPresenter.Background，
        // 因此在本轮消息末尾同步模板部件，确保窗口级主题资源不会被系统主题覆盖。
        DispatcherQueue.TryEnqueue(() =>
        {
            if (FindVisualChild<ContentPresenter>(button) is { } presenter)
                presenter.Background = brush;
        });
    }

    private void ApplyButtonVisual(Button button, Brush background, Brush foreground)
    {
        button.Background = background;
        button.Foreground = foreground;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (FindVisualChild<ContentPresenter>(button) is not { } presenter) return;
            presenter.Background = background;
            presenter.Foreground = foreground;
        });
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void ShowScriptsPage()
    {
        if (ScriptsPage == null || RunsPage == null) return;
        WorkArea.Visibility = Visibility.Visible;
        HelpPageHost.Visibility = Visibility.Collapsed;
        ScriptsPage.Visibility = Visibility.Visible;
        RunsPage.Visibility = Visibility.Collapsed;
        RefreshButtonRestingVisuals();
    }

    private void ShowRunsPage()
    {
        if (ScriptsPage == null || RunsPage == null) return;
        WorkArea.Visibility = Visibility.Visible;
        HelpPageHost.Visibility = Visibility.Collapsed;
        ScriptsPage.Visibility = Visibility.Collapsed;
        RunsPage.Visibility = Visibility.Visible;
        RefreshButtonRestingVisuals();
        _historyViewModel.Load(ViewModel.SelectedScript?.Id);
    }

    private void ShowHelpPage()
    {
        WorkArea.Visibility = Visibility.Collapsed;
        HelpPageHost.Visibility = Visibility.Visible;
        _helpPage.Refresh();
        RefreshButtonRestingVisuals();
    }

    /// <summary>把侧栏选择投影到设计稿脚本信息栏、终端命令预览和状态栏。</summary>
    private void RefreshSelectedScriptPresentation()
    {
        if (SelectedScriptNameText == null) return;

        var script = _scriptListViewModel.SelectedScript;
        var item = _scriptListViewModel.SelectedItem;
        var hasScript = script != null;
        EditButton.IsEnabled = hasScript;
        RefreshButtonRestingVisuals();

        if (script == null)
        {
            SelectedScriptNameText.Text = NoScriptSelectedText;
            SelectedScriptMetadataText.Text = SelectScriptHintText;
            ToolTipService.SetToolTip(SelectedScriptMetadataText, null);
            StatusBadgeText.Text = NotRunText;
            StatusBadgeText.Foreground = Views.ThemeBrushes.Get("TextMutedBrush", 0xFF8A949E);
            StatusBadgeBorder.Background = Views.ThemeBrushes.Get("ControlSurfaceBrush", 0xFF34373B);
            TerminalCommandText.Text = TerminalCommandPlaceholderText;
            ToolTipService.SetToolTip(TerminalCommandText, null);
            TerminalTabText.Text = _localization["Terminal_TabEmpty"];
            StatusWorkingDirectoryText.Text = StatusNoDirectoryText;
            ToolTipService.SetToolTip(StatusWorkingDirectoryText, null);
            RefreshLastRunPresentation();
            return;
        }

        var interpreter = ResolveInterpreter(script);
        var interpreterText = InterpreterDisplayName(interpreter);
        var category = string.IsNullOrWhiteSpace(script.Category)
            ? _localization["Category_Uncategorized"]
            : script.Category.Trim();
        SelectedScriptNameText.Text = Path.GetFileName(script.FilePath);
        var metadataFull = string.Join("  ·  ",
            new[] { script.FilePath, interpreterText, category }.Where(value => !string.IsNullOrWhiteSpace(value)));
        SelectedScriptMetadataText.Text = string.Join("  ·  ",
            new[] { Views.PathDisplay.MiddleEllipsis(script.FilePath, 72), interpreterText, category }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        ToolTipService.SetToolTip(SelectedScriptMetadataText, metadataFull);

        var status = item?.Status ?? RunStatus.NotRun;
        StatusBadgeText.Text = item?.StatusText ?? NotRunText;
        StatusBadgeText.Foreground = Views.ThemeBrushes.Get(status switch
        {
            RunStatus.Running => "StatusRunningBrush",
            RunStatus.Success => "StatusSuccessBrush",
            RunStatus.Failed => "StatusErrorBrush",
            RunStatus.Killed => "StatusStoppedBrush",
            _ => "TextMutedBrush",
        }, 0xFF8A949E);
        StatusBadgeBorder.Background = Views.ThemeBrushes.Get(status switch
        {
            RunStatus.Running => "StatusRunningSurfaceBrush",
            RunStatus.Success => "StatusSuccessSurfaceBrush",
            RunStatus.Failed => "StatusErrorSurfaceBrush",
            RunStatus.Killed => "StatusStoppedSurfaceBrush",
            _ => "ControlSurfaceBrush",
        }, 0xFF34373B);

        TerminalCommandText.Text = BuildCommandPreview(script);
        ToolTipService.SetToolTip(TerminalCommandText, TerminalCommandText.Text);
        TerminalTabText.Text = string.Format(_localization["Terminal_TabFormat"], Path.GetFileName(script.FilePath));
        var workingDirectory = ResolveWorkingDirectory(script);
        var workingDirectoryDisplay = string.IsNullOrWhiteSpace(workingDirectory)
            ? _localization["Value_None"]
            : Views.PathDisplay.MiddleEllipsis(workingDirectory, 58);
        StatusWorkingDirectoryText.Text = string.Format(_localization["Status_DirectoryFormat"], workingDirectoryDisplay);
        ToolTipService.SetToolTip(StatusWorkingDirectoryText, workingDirectory);
        RefreshLastRunPresentation();
    }

    private Interpreter? ResolveInterpreter(Script script)
    {
        Interpreter? interpreter = null;
        if (script.InterpreterId is int interpreterId)
            interpreter = _fileTreeViewModel.Interpreters.FirstOrDefault(value => value.Id == interpreterId);
        return interpreter
            ?? _fileTreeViewModel.Interpreters.FirstOrDefault(value => value.IsDefault)
            ?? _fileTreeViewModel.Interpreters.FirstOrDefault();
    }

    private string InterpreterDisplayName(Interpreter? interpreter)
    {
        if (interpreter == null) return _localization["Sidebar_NoInterpreter"];
        if (!string.IsNullOrWhiteSpace(interpreter.Version)
            && !interpreter.Name.Contains(interpreter.Version, StringComparison.OrdinalIgnoreCase))
            return $"{interpreter.Name} {interpreter.Version}";
        return interpreter.Name;
    }

    private static string? ResolveWorkingDirectory(Script script) =>
        !string.IsNullOrWhiteSpace(script.WorkingDirectory)
            ? script.WorkingDirectory
            : Path.GetDirectoryName(script.FilePath);

    private static string BuildCommandPreview(Script script)
    {
        var fileName = Path.GetFileName(script.FilePath);
        if (fileName.Contains(' ')) fileName = $"\"{fileName}\"";
        return string.IsNullOrWhiteSpace(script.Arguments)
            ? $"> python {fileName}"
            : $"> python {fileName} {script.Arguments}";
    }

    private void RefreshLastRunPresentation()
    {
        if (_lastRunStatus == null || _lastRunTime == null)
        {
            LastRunResultText.Text = StatusNoRunText;
            LastRunTimeText.Text = string.Empty;
            LastRunTimeText.Visibility = Visibility.Collapsed;
            LastRunTimeSeparatorText.Visibility = Visibility.Collapsed;
            return;
        }

        var statusText = _lastRunStatus.Value switch
        {
            RunStatus.Success => _localization["StatusBadge_Success"],
            RunStatus.Failed => _localization["StatusBadge_Failed"],
            RunStatus.Killed => _localization["StatusBadge_Killed"],
            _ => _localization["StatusBadge_Running"],
        };
        LastRunResultText.Text = statusText;
        LastRunTimeText.Text = _lastRunTime.Value.ToString("HH:mm:ss");
        LastRunTimeText.Visibility = Visibility.Visible;
        LastRunTimeSeparatorText.Visibility = Visibility.Visible;
    }

    private void RefreshTerminalTabHeaders()
    {
        var selectedScript = _scriptListViewModel.SelectedScript;
        TerminalTabText.Text = selectedScript == null
            ? _localization["Terminal_TabEmpty"]
            : string.Format(_localization["Terminal_TabFormat"], Path.GetFileName(selectedScript.FilePath));

        foreach (var pair in _tabSessions)
        {
            var tabName = ResolveTerminalTabName(pair.Value);
            pair.Key.Header = CreateTerminalTabHeader(tabName);
            AutomationProperties.SetName(pair.Key,
                string.Format(_localization["Terminal_TabFormat"], tabName));
        }
    }

    private string ResolveTerminalTabName(TerminalSessionViewModel viewModel) =>
        _scriptService.GetById(viewModel.ScriptId) is { } script
            ? Path.GetFileName(script.FilePath)
            : viewModel.DisplayName;

    private StackPanel CreateTerminalTabHeader(string scriptName)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        header.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = Views.ThemeBrushes.Get("AccentBrush", 0xFF4CC38A),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock
        {
            Text = string.Format(_localization["Terminal_TabFormat"], scriptName),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return header;
    }

    private void OnClosed(object? sender, WindowEventArgs args)
    {
        _isClosing = true;
        _startupUpdateCancellation.Cancel();
        var startupTask = _startupUpdateTask;
        if (startupTask.IsCompleted)
        {
            _ = startupTask.Exception;
            _startupUpdateCancellation.Dispose();
        }
        else
        {
            _ = startupTask.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    _startupUpdateCancellation.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        if (_kbHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_kbHook);
            _kbHook = IntPtr.Zero;
            _kbHookDelegate = null;
        }
        _localization.LanguageChanged -= OnLanguageChanged;
        _scriptListViewModel.DetachLocalization();
        _fileTreeViewModel.DetachLocalization();
        _historyViewModel.DetachLocalization();
        _helpPage.SettingsRequested -= OnHelpSettingsRequested;
        _helpPage.Dispose();
        _navigation.Navigated -= OnShellNavigated;
        _watcher.Changed -= OnWatcherChanged;
        _scriptListViewModel.PropertyChanged -= OnListSelectionChanged;
        _coordinator.StatusChanged -= OnRunStatusChanged;

        // 先释放全部终端会话（Dispose 触发 Job Object 孤儿防护），再退订壳 VM；
        // 顺序约束：ShutdownAll 内 FinishRun 依赖尚未 Dispose 的单例服务（App 的
        // Closed 订阅晚于本窗口，Services.Dispose 在其后执行）
        _coordinator.ShutdownAll();
        ViewModel.Shutdown();
        // 设置刷盘不再手工约定：App 在窗口关闭后 Dispose ServiceProvider，
        // 级联释放 IDisposable 单例（JsonSettingsService.Dispose 内部先 Flush 再停 timer）
    }

    // ==== 运行编排：入口 / 标签 / 状态机 ====

    /// <summary>工具栏「运行」：运行当前选中脚本（失败路径可能弹对话框，纳入互斥）。</summary>
    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (_dialogInFlight) return;
        _dialogInFlight = true;
        try
        {
            if (ViewModel.SelectedScript is { } script)
                await RunScriptAsync(script);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("Run", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    /// <summary>工具栏「停止」：对选中脚本发起停止编排（\x03 → 2s → Kill）。</summary>
    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedScript is { } script && _coordinator.IsRunning(script.Id))
            _coordinator.RequestStop(script.Id);
    }

    /// <summary>列表项右键/更多菜单「运行」（flyout 入口：失败路径会弹对话框，
    /// 须经 FlyoutDismissGuard 让过收起动画再弹窗，评审修复 5）。</summary>
    private async void OnSidebarRun(object? sender, ScriptListItemViewModel item)
    {
        if (_dialogInFlight) return;
        _dialogInFlight = true; // 立即置位：delay 在标志保护内（TOCTOU 契约）
        try
        {
            await DialogHostHelper.ShowAfterFlyoutDismissAsync(sender as DependencyObject);
            await RunScriptAsync(item.Script);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("SidebarRun", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    /// <summary>文件树右键「运行」已登记脚本（flyout 入口：同评审修复 5 守卫）。</summary>
    private async void OnTreeRunRequested(Script script)
    {
        if (_dialogInFlight) return;
        _dialogInFlight = true; // 立即置位：delay 在标志保护内（TOCTOU 契约）
        try
        {
            await DialogHostHelper.ShowAfterFlyoutDismissAsync(null); // 树菜单 sender 非可视树成员，仅让动画周期
            await RunScriptAsync(script);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("TreeRun", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    /// <summary>运行一次脚本（调用方已持有 _dialogInFlight 互斥）：
    /// TryStart 成功 → 懒创建标签+WebView2；失败 → 错误提示（无解释器时引导去设置）。</summary>
    private async Task RunScriptAsync(Script script)
    {
        TryStartResult result;
        try
        {
            result = _coordinator.TryStart(script);
        }
        catch (Exception ex)
        {
            // 数据层故障（StartRun 落库失败等）：友好提示，不崩进程
            DebugWriteUnexpected("Run.TryStart", ex);
            await _notifications.NotifyRunErrorAsync(Content.XamlRoot, "Error_SaveFailed");
            return;
        }

        if (result.IsSuccess)
        {
            CreateTab(result.ViewModel!);
            return;
        }

        if (result.ErrorKey == "Run_NoInterpreter")
        {
            // A-24：无解释器友好提示（INotificationService，不暴露技术细节）；确认后打开设置
            if (await _notifications.NotifyNoInterpreterAsync(Content.XamlRoot))
                await ShowSettingsDialogAsync();
            return;
        }

        if (result.ErrorKey == "Run_ScriptNotFound")
        {
            // A-25：脚本文件已被移动/删除 → [重新选择文件] [移除记录]
            var choice = await _notifications.NotifyScriptMissingAsync(Content.XamlRoot);
            if (choice == NotifyChoice.Primary)
            {
                await ShowScriptEditDialogAsync(script); // 编辑对话框内重选文件路径
            }
            else if (choice == NotifyChoice.Secondary)
            {
                await DeleteScriptRecordSilentAsync(script.Id);
            }
            return;
        }

        await _notifications.NotifyRunErrorAsync(Content.XamlRoot, result.ErrorKey!);
    }

    /// <summary>移除脚本登记（通知引导路径：不再二次确认，用户已在对话框内选择）。</summary>
    private async Task DeleteScriptRecordSilentAsync(int scriptId)
    {
        try
        {
            _scriptService.Delete(scriptId);
            _scriptListViewModel.Load();
            _fileTreeViewModel.Load();
            _historyViewModel.Load(null);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("NotifyRemoveRecord", ex);
        }
    }

    /// <summary>懒创建标签：此刻才 new WebView2（启动/空工作区不建任何实例）。</summary>
    private void CreateTab(TerminalSessionViewModel viewModel)
    {
        // 在导航前保存主题，使终端页面可通过查询参数使用正确首屏颜色，避免闪烁。
        viewModel.ApplyTheme(_isLightTheme);
        var webView = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        var tabName = ResolveTerminalTabName(viewModel);
        var tab = new TabViewItem
        {
            Header = CreateTerminalTabHeader(tabName),
            Content = webView,
            IsClosable = true,
            MinHeight = 32,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        // TabView 偶发标签头 UIA 消失留痕的缓解（Phase E）：显式标注
        // AutomationProperties.Name，使容器回收后 peer 仍有稳定名称来源
        AutomationProperties.SetName(tab,
            string.Format(_localization["Terminal_TabFormat"], tabName));

        // §6.11 标签创建淡入（评审修 6）：新标签内容入场时 EntranceThemeTransition
        // （系统时长近似，与弹窗/树两处既有口径一致）
        tab.ContentTransitions = new TransitionCollection { new EntranceThemeTransition() };

        // 状态栏联动：活跃标签的会话状态同步到壳 VM（具名 handler，关标签退订）
        System.ComponentModel.PropertyChangedEventHandler statusHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(TerminalSessionViewModel.Status) && ReferenceEquals(Tabs.SelectedItem, tab))
                ViewModel.Status = viewModel.Status;
        };
        viewModel.PropertyChanged += statusHandler;
        _tabStatusHandlers[tab] = statusHandler;

        _tabSessions[tab] = viewModel;
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
        IdleTerminalTab.Visibility = Visibility.Collapsed;
        WorkspaceHint.Visibility = Visibility.Collapsed;
        _navigation.Navigate(ShellPage.Scripts);
        if (_scriptService.GetById(viewModel.ScriptId) is { } script)
            TerminalCommandText.Text = BuildCommandPreview(script);

        _ = InitializeTabAsync(tab, viewModel, webView);
    }

    /// <summary>标签内 WebView2 初始化；失败时经编排器落库并回收标签。
    /// 评审修复 3：进程从未启动 → AbortNotStarted 映射 Failed（不再记 Killed）；
    /// 进程已启动（导航后故障）→ CloseTab/CloseSession 仍映射 Killed；
    /// 若失败由关标签/关窗口触发（Shutdown 已置位）→ 终结已由 CloseSession 完成，不重复落库。</summary>
    private async Task InitializeTabAsync(TabViewItem tab, TerminalSessionViewModel viewModel, WebView2 webView)
    {
        try
        {
            await viewModel.InitializeAsync(webView);
        }
        catch (Exception ex)
        {
            if (viewModel.IsShutdownRequested)
            {
                // 关标签/关窗口在初始化窗口内发生：CloseSession 已终结并 Shutdown
                DebugWriteError("WebView2 初始化因窗口拆卸中断", ex);
                return;
            }

            ViewModel.SetLocalizedStatus("Error_WebViewInitFailed");
            DebugWriteError("WebView2 初始化失败", ex);

            if (!viewModel.IsSessionStarted)
            {
                _coordinator.AbortNotStarted(viewModel); // 未启动 → Failed；内部已 Shutdown
                // 会话从未启动即初始化失败：典型为 WebView2 运行时缺失，友好引导下载
                if (!_dialogInFlight)
                {
                    _dialogInFlight = true;
                    try
                    {
                        await _notifications.NotifyWebView2MissingAsync(Content.XamlRoot);
                    }
                    finally
                    {
                        _dialogInFlight = false;
                    }
                }
            }

            CloseTab(tab); // 已启动路径在此经 CloseSession → Killed；Shutdown 幂等
        }
    }

    private void OnTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab is { } tab)
            CloseTab(tab);
    }

    /// <summary>关闭标签：运行中会话按停止处理（状态 Killed），随后 Dispose 会话并移除标签。</summary>
    private void CloseTab(TabViewItem tab)
    {
        if (_tabStatusHandlers.TryGetValue(tab, out var handler))
        {
            if (_tabSessions.TryGetValue(tab, out var vm))
                vm.PropertyChanged -= handler;
            _tabStatusHandlers.Remove(tab);
        }

        if (_tabSessions.Remove(tab, out var session))
            _coordinator.CloseSession(session); // 运行中 → Killed 落库；会话 Dispose（Job Object 孤儿防护）

        Tabs.TabItems.Remove(tab);
        if (Tabs.TabItems.Count == 0)
        {
            IdleTerminalTab.Visibility = Visibility.Visible;
            WorkspaceHint.Visibility = Visibility.Visible;
            ViewModel.SetLocalizedStatus("Status_Exited");
            TerminalCommandText.Text = _scriptListViewModel.SelectedScript is { } selected
                ? BuildCommandPreview(selected)
                : TerminalCommandPlaceholderText;
        }
    }

    /// <summary>
    /// 标签切换：状态栏、侧栏选中项及顶部脚本信息全部跟随活跃终端。
    /// ScriptId 是标签与脚本上下文的稳定关联，切换时不依赖标签标题文本。
    /// </summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (args.AddedItems.Count > 0 && args.AddedItems[0] is TabViewItem tab && _tabSessions.TryGetValue(tab, out var vm))
        {
            ViewModel.Status = vm.Status;
            if (_scriptListViewModel.SelectByScriptId(vm.ScriptId))
            {
                // SelectedItem 未变化时 PropertyChanged 不会再次触发，显式刷新可覆盖
                // “切回同脚本的新会话”以及标签状态刚变化的场景。
                ViewModel.SelectedScript = _scriptListViewModel.SelectedScript;
                ViewModel.SelectedScriptRunning = _coordinator.IsRunning(vm.ScriptId);
                _historyViewModel.Load(vm.ScriptId);
                RefreshSelectedScriptPresentation();
            }
            else if (_scriptService.GetById(vm.ScriptId) is { } script)
            {
                // 数据层仍可读但侧栏暂时不可用时，至少保证命令预览与工作目录不陈旧。
                TerminalCommandText.Text = BuildCommandPreview(script);
                StatusWorkingDirectoryText.Text = string.Format(_localization["Status_DirectoryFormat"],
                    ResolveWorkingDirectory(script));
            }
        }

        // §6.11 选中脚本切换 → 右侧淡入 150ms（评审修 6）：EntranceThemeTransition 仅在元素
        // 入场时播放（标签创建已在 CreateTab 设置），选中切换用等价的透明度淡入重放
        // （系统时长近似口径，与弹窗/树两处既有实现一致）
        ReplayWorkspaceFadeIn();
    }

    /// <summary>工作区淡入重放（§6.11，评审修 6）：150ms 透明度 0→1。
    /// 留痕：XAML 声明式过渡在 TabView 内容切换场景无挂载点（内容宿主为模板内
    /// ContentPresenter，非可设 ContentTransitions 的容器），且 WAS 1.5 工具链对部分
    /// 过渡属性存在静默退出缺陷（见侧栏树 IsAnimatingNewContainers 留痕），故采用代码侧
    /// Storyboard 降级实现；若未来工具链修复可回归 EntranceThemeTransition。</summary>
    private void ReplayWorkspaceFadeIn()
    {
        try
        {
            var animation = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(150)),
            };
            Storyboard.SetTarget(animation, Tabs);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch (Exception ex)
        {
            // 动画故障不影响功能：仅留痕
            DebugWriteUnexpected("WorkspaceFadeIn", ex);
        }
    }

    /// <summary>运行编排状态广播（UI 线程）：侧栏徽章 + 壳 VM 状态机 + 停止按钮高亮。
    /// 评审修复 2：状态栏文案仅当广播 scriptId 对应当前活跃标签的会话才更新，
    /// 后台标签的终结事件不得覆盖状态栏；徽章与 SelectedScriptRunning 不受此门控。</summary>
    private void OnRunStatusChanged(int scriptId, RunStatus status)
    {
        _scriptListViewModel.SetRunStatus(scriptId, status);

        // 历史面板（Phase E）：选中脚本的任一状态广播后重读最近记录（含运行中条目）
        if (ViewModel.SelectedScript?.Id == scriptId)
        {
            ViewModel.SelectedScriptRunning = status == RunStatus.Running;
            _historyViewModel.Load(scriptId);
            RefreshSelectedScriptPresentation();
        }

        if (status is RunStatus.Success or RunStatus.Failed or RunStatus.Killed)
        {
            _lastRunStatus = status;
            _lastRunTime = DateTime.Now;
            RefreshLastRunPresentation();
        }

        // 停止按钮高亮：与设计稿保持灰底红字；运行中仅提高危险色底的可见度。
        StopButton.Background = ViewModel.CanStop
            ? Views.ThemeBrushes.Get("DangerSurfaceBrush", 0x2EFF8F8F)
            : Views.ThemeBrushes.Get("DisabledButtonBrush", 0xFF24282C);

        GlobalStatusDot.Fill = Views.ThemeBrushes.Get(status switch
        {
            RunStatus.Running => "StatusRunningBrush",
            RunStatus.Success => "StatusSuccessBrush",
            RunStatus.Failed => "StatusErrorBrush",
            RunStatus.Killed => "StatusStoppedBrush",
            _ => "AccentBrush",
        }, 0xFF35A875);

        if (status == RunStatus.Failed && _settingsService.Current.NotifyOnFail)
            _ = NotifyFailedRunAsync(scriptId);

        // 门控：仅活跃标签的会话终结/启动才驱动状态栏
        var activeVm = Tabs.SelectedItem is TabViewItem activeTab && _tabSessions.TryGetValue(activeTab, out var vm)
            ? vm
            : null;
        if (activeVm == null || activeVm.ScriptId != scriptId) return;

        ViewModel.SetLocalizedStatus(
            status switch
            {
                RunStatus.Running => "Status_Connected",
                RunStatus.Success => "Status_RunFinished",
                RunStatus.Failed => "Status_RunFailed",
                RunStatus.Killed => "Status_RunKilled",
                _ => "Status_Initializing",
            });
    }

    /// <summary>
    /// 失败提醒与其他 ContentDialog 串行显示；等待现有对话框关闭，不在其上叠第二个弹窗。
    /// 多脚本同时失败时，各 continuation 在 UI 线程依次取得 _dialogInFlight。
    /// </summary>
    private async Task NotifyFailedRunAsync(int scriptId)
    {
        while (_dialogInFlight && !_isClosing)
            await Task.Delay(100);

        if (_isClosing || !_settingsService.Current.NotifyOnFail) return;
        var script = _scriptService.GetById(scriptId);
        if (script == null) return;

        _dialogInFlight = true;
        try
        {
            await _notifications.NotifyRunFailedAsync(Content.XamlRoot, script.Name);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("RunFailedNotification", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    /// <summary>列表选中变化 → 壳 VM 运行状态机（选中脚本 + 是否运行中）+ 历史面板刷新（Phase E）。</summary>
    private void OnListSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ScriptListViewModel.SelectedItem)) return;
        ViewModel.SelectedScript = _scriptListViewModel.SelectedScript;
        ViewModel.SelectedScriptRunning = _scriptListViewModel.SelectedScript is { } s && _coordinator.IsRunning(s.Id);
        _historyViewModel.Load(ViewModel.SelectedScript?.Id);

        StopButton.Background = ViewModel.CanStop
            ? Views.ThemeBrushes.Get("DangerSurfaceBrush", 0x2EFF8F8F)
            : Views.ThemeBrushes.Get("GhostButtonBrush", 0x12FFFFFF);
        RefreshSelectedScriptPresentation();
    }

    private async void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (_dialogInFlight || _scriptListViewModel.SelectedScript is not { } script) return;
        _dialogInFlight = true;
        try
        {
            await ShowScriptEditDialogAsync(script);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("HeaderEdit", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    // ==== 侧栏脚本列表：正式入口 ====

    private async void OnSidebarEdit(object? sender, ScriptListItemViewModel item)
    {
        if (_dialogInFlight) return; // 在途互斥
        _dialogInFlight = true; // 立即置位：200ms 延迟窗口内拒绝并发入口（TOCTOU 修复）
        try
        {
            await DialogHostHelper.ShowAfterFlyoutDismissAsync(sender as DependencyObject);
            await ShowScriptEditDialogAsync(item.Script);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("SidebarEdit", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    private async void OnSidebarDelete(object? sender, ScriptListItemViewModel item)
    {
        if (_dialogInFlight) return; // 在途互斥
        _dialogInFlight = true; // 立即置位：覆盖 200ms 延迟窗口（TOCTOU 修复）
        try
        {
            var script = item.Script;
            await DialogHostHelper.ShowAfterFlyoutDismissAsync(sender as DependencyObject);
            await DeleteScriptWithConfirmAsync(script);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("SidebarDelete", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    /// <summary>删除脚本登记（二次确认；文案明示「只移除记录，不删除文件」）；
    /// 供右键/更多菜单与 Delete 快捷键共用（调用方已持有 _dialogInFlight）。</summary>
    private async Task DeleteScriptWithConfirmAsync(Script script)
    {
        var confirm = new ContentDialog
        {
            Title = _localization["DeleteConfirm_Title"],
            Content = _localization["DeleteConfirm_Content"],
            PrimaryButtonText = _localization["Button_Delete"],
            SecondaryButtonText = _localization["Button_Cancel"],
            DefaultButton = ContentDialogButton.Secondary,
        };
        PrepareDialog(confirm);

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            _scriptService.Delete(script.Id);
            _scriptListViewModel.Load();
            _fileTreeViewModel.Load(); // 徽章计数同步
        }
        catch (Exception ex)
        {
            // 数据层故障：删除静默失败并记日志，不崩进程
            DebugWriteUnexpected("DeleteScript", ex);
        }
    }

    /// <summary>文件树点击已登记 .py：清除搜索并选中列表对应项。</summary>
    private void OnTreeRegisteredScript(string filePath)
    {
        try
        {
            _scriptListViewModel.ClearSearch();
            _scriptListViewModel.Load(filePath);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("TreeRegisteredScript", ex);
        }
    }

    /// <summary>文件树点击尚未登记的 .py：自动同步配置目录，再按路径选中。</summary>
    private async void OnTreeUnregisteredScript(string filePath)
    {
        try
        {
            await ImportConfiguredScriptsAsync();
            if (_isClosing) return;
            _scriptListViewModel.ClearSearch();
            _scriptListViewModel.Load(filePath);
            await _fileTreeViewModel.RefreshAsync();
            RefreshSelectedScriptPresentation();
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("TreeUnregisteredScript", ex);
        }
    }

    // ==== 设置弹窗 ====

    private async void OnSettingsClick(object sender, RoutedEventArgs e) =>
        await OpenSettingsGuardedAsync(sender as DependencyObject);

    private async void OnHelpSettingsRequested(object? sender, HelpSettingsSection section) =>
        await OpenSettingsGuardedAsync(
            HelpPageHost,
            focusInterpreters: section == HelpSettingsSection.Interpreters,
            focusScriptPaths: section == HelpSettingsSection.ScriptPaths);

    /// <summary>打开设置弹窗（在途互斥 + flyout 来源守卫）：标题栏按钮与
    /// 空状态引导按钮（Phase E）共用入口；focusInterpreters 直达解释器分组（评审修 5）。</summary>
    private async Task OpenSettingsGuardedAsync(
        DependencyObject? uiContext,
        bool focusInterpreters = false,
        bool focusScriptPaths = false)
    {
        if (_dialogInFlight) return; // 在途互斥：设置弹窗不得与其他对话框并发
        _dialogInFlight = true; // 立即置位：200ms 延迟窗口内拒绝并发入口（TOCTOU 修复）
#if DEBUG
        DebugLog.WriteLine($"Window: OpenSettingsGuarded 进入 focusInterpreters={focusInterpreters}");
#endif
        try
        {
            await DialogHostHelper.ShowAfterFlyoutDismissAsync(uiContext);
            await ShowSettingsDialogAsync(focusInterpreters, focusScriptPaths);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("Settings", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    /// <summary>空状态引导「打开设置」（Phase E；非 flyout 来源，无延迟）。</summary>
    private async void OnSidebarOpenSettings(object? sender, EventArgs e) =>
        await OpenSettingsGuardedAsync(null);

    /// <summary>「无解释器」空状态引导（评审修 5）：直达设置弹窗解释器分组。</summary>
    private async void OnSidebarOpenInterpreterSettings(object? sender, EventArgs e) =>
        await OpenSettingsGuardedAsync(null, focusInterpreters: true);

    /// <summary>标志生命周期归调用入口所有（检查通过即置位、finally 复位），本方法不再自行置位。</summary>
    private async Task ShowSettingsDialogAsync(bool focusInterpreters = false, bool focusScriptPaths = false)
    {
        var dialog = _settingsDialogFactory();
        PrepareDialog(dialog);
        dialog.SetOwnerWindow(this); // 文件夹/文件选择器需要宿主窗口句柄
        dialog.FocusInterpretersOnOpen = focusInterpreters; // 评审修 5：无解释器引导直达解释器分组
        dialog.FocusScriptPathsOnOpen = focusScriptPaths;
        await dialog.ShowAsync();

        // 设置可能改动了脚本路径：同步目录后刷新列表/树（含底部解释器区）并对齐监听。
        await ImportConfiguredScriptsAsync();
        if (_isClosing) return;
        _scriptListViewModel.Load();
        await _fileTreeViewModel.RefreshAsync();
        _watcher.SyncFromStore();
        RefreshSelectedScriptPresentation();
    }

    // ==== 脚本登记对话框 ====

    /// <summary>标志生命周期归调用入口所有（检查通过即置位、finally 复位），本方法不再自行置位。</summary>
    private async Task ShowScriptEditDialogAsync(Script existing)
    {
        var dialog = _scriptEditDialogFactory();
        PrepareDialog(dialog);
        dialog.SetOwnerWindow(this); // 「浏览…」按钮的 FileOpenPicker 需要宿主窗口句柄
        dialog.Prepare(existing);
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            _scriptListViewModel.Load(dialog.SavedFilePath);
            _fileTreeViewModel.Load(); // 徽章计数同步
            RefreshSelectedScriptPresentation();
        }
    }

    // ==== 侧栏拖拽调宽（180~500 DIP，写回 AppSettings.SidebarWidth 单一真相源） ====

    private void OnThumbPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _thumbDragging = true;
        _dragStartX = e.GetCurrentPoint((UIElement)Content).Position.X;
        _dragStartWidth = SidebarColumn.Width.Value;
        if (sender is UIElement el)
            el.CapturePointer(e.Pointer);
    }

    private void OnThumbPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_thumbDragging) return;
        var x = e.GetCurrentPoint((UIElement)Content).Position.X;
        var width = Math.Clamp(_dragStartWidth + (x - _dragStartX), SidebarWidthMin, SidebarWidthMax);
        SidebarColumn.Width = new GridLength(width);
    }

    private void OnThumbPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_thumbDragging) return;
        _thumbDragging = false;
        if (sender is UIElement el)
            el.ReleasePointerCapture(e.Pointer);

        // 拖拽结束写回同一键；Released 即时 Flush 落盘（评审修复：替代防抖 Save，
        // 消除拖拽后窗口被强杀导致的宽度丢失；窗口关闭时 Dispose 链 Flush 仍兜底）
        var width = Math.Clamp(SidebarColumn.Width.Value, SidebarWidthMin, SidebarWidthMax);
        var persistedWidth = (int)Math.Round(width);
        _settingsService.Update(settings => settings.SidebarWidth = persistedWidth);
        _settingsService.Flush();
#if DEBUG
        DebugLog.WriteLine($"Window: 侧栏宽度拖拽写回 {persistedWidth} DIP");
#endif
    }

    // ==== Phase E：记事本 / 收藏 / 刷新 / 监听 / 历史 / 首启向导 ====

    /// <summary>列表项「在记事本中打开」（Phase E）。</summary>
    private async void OnSidebarNotebook(object? sender, ScriptListItemViewModel item) =>
        await OpenInNotepadAsync(item.Script.FilePath);

    /// <summary>文件树「在记事本中打开」（Phase E）。</summary>
    private async void OnTreeNotebookRequested(string filePath) =>
        await OpenInNotepadAsync(filePath);

    /// <summary>拉起 notepad.exe；文件不存在时友好提示（不暴露技术细节）。</summary>
    private async Task OpenInNotepadAsync(string filePath)
    {
        if (File.Exists(filePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{filePath}\"")
                {
                    UseShellExecute = false,
                });
                return;
            }
            catch (Exception ex)
            {
                DebugWriteUnexpected("Notepad", ex);
            }
        }

        if (_dialogInFlight) return;
        _dialogInFlight = true;
        try
        {
            await _notifications.NotifyFileMissingAsync(Content.XamlRoot);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("NotebookNotify", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    /// <summary>列表项收藏切换（Phase E）。</summary>
    private void OnSidebarFavoriteToggle(object? sender, ScriptListItemViewModel item) =>
        ToggleFavoriteAndReload(item.Script);

    /// <summary>文件树收藏切换（Phase E）。</summary>
    private void OnTreeFavoriteToggleRequested(Script script) =>
        ToggleFavoriteAndReload(script);

    private void ToggleFavoriteAndReload(Script script)
    {
        try
        {
            _scriptService.ToggleFavorite(script.Id, !script.IsFavorite);
            _scriptListViewModel.Load();
            _fileTreeViewModel.Load(); // 收藏虚拟节点与菜单文案重建
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("FavoriteToggle", ex);
        }
    }

    /// <summary>侧栏手动兜底刷新（Phase E）：重建 watcher + 列表 + 文件树（加载态 ProgressRing）。</summary>
    private async void OnSidebarRefresh(object? sender, EventArgs e)
    {
        try
        {
            _watcher.Resync();
            _watcher.SyncFromStore();
            await ImportConfiguredScriptsAsync();
            if (_isClosing) return;
            _scriptListViewModel.Load();
            await _fileTreeViewModel.RefreshAsync();
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("SidebarRefresh", ex);
        }
    }

    /// <summary>目录监听去抖广播（线程池线程）：封送 UI 线程后重建树与列表。
    /// 失效路径节点的置灰由树重建时的 Directory.Exists 检查完成。</summary>
    private void OnWatcherChanged()
    {
        if (!DispatcherQueue.TryEnqueue(() => _ = RefreshAfterDiskChangeCoalescedAsync()))
        {
            // 窗口已关闭等：丢弃本次广播
        }
    }

    /// <summary>磁盘变更重复合并（评审修 12）：刷新在途时置脏，完成后补一轮，
    /// 避免批量变更多波重复重建与 ProgressRing 闪烁（仅 UI 线程读写）。</summary>
    private bool _diskRefreshInFlight;
    private bool _diskRefreshPending;

    private async Task RefreshAfterDiskChangeCoalescedAsync()
    {
        if (_diskRefreshInFlight)
        {
            _diskRefreshPending = true;
            return;
        }

        _diskRefreshInFlight = true;
        try
        {
            do
            {
                _diskRefreshPending = false;
                await RefreshAfterDiskChangeAsync();
            } while (_diskRefreshPending); // 在途期间又来广播：补一轮即可收敛
        }
        finally
        {
            _diskRefreshInFlight = false;
        }
    }

    private async Task RefreshAfterDiskChangeAsync()
    {
        try
        {
            await ImportConfiguredScriptsAsync();
            if (_isClosing) return;
            _scriptListViewModel.Load();
            await _fileTreeViewModel.RefreshAsync();
#if DEBUG
            DebugLog.WriteLine("Window: 目录监听变更，已重建树与列表");
#endif
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("WatcherRefresh", ex);
        }
    }

    /// <summary>递归导入所有已配置脚本目录。枚举与 SQLite 写入放在线程池，避免阻塞 UI。</summary>
    private Task<int> ImportConfiguredScriptsAsync() => Task.Run(() =>
    {
        var paths = _scriptPathService.GetAll()
            .Where(path => path.Enabled)
            .Select(path => path.Path)
            .ToList();
        return _scriptService.ImportFromPaths(paths);
    });

    /// <summary>兼容旧数据：启动后自动补登配置目录中尚未登记的 .py。</summary>
    private async Task ImportConfiguredScriptsOnStartupAsync()
    {
        try
        {
            var imported = await ImportConfiguredScriptsAsync();
            if (_isClosing || imported == 0) return;
            _scriptListViewModel.Load();
            await _fileTreeViewModel.RefreshAsync();
            RefreshSelectedScriptPresentation();
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("StartupDirectoryImport", ex);
        }
    }

    /// <summary>历史详情展示上限（字符）：与落库口径一致保留尾部（TailOutputBuffer 环形截断，评审修 13）。</summary>
    private const int HistoryDetailMaxChars = 64 * 1024;

    /// <summary>历史条目点击：展示完整输出（去 ANSI，本地时间口径见 VM）；受在途互斥。
    /// 超长输出仅渲染尾部 64KB（评审修 13：与落库尾部截断口径一致，防 200KB 全文本渲染卡 UI）。</summary>
    private async void OnHistoryItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RunHistoryItemViewModel item) return;
        if (_dialogInFlight) return;
        _dialogInFlight = true;
        try
        {
            var full = item.FullOutput.Length > 0 ? item.FullOutput : item.Summary;
            string display;
            if (full.Length > HistoryDetailMaxChars)
            {
                display = string.Format(_localization["History_Detail_Truncated"], HistoryDetailMaxChars / 1024)
                    + "\n\n" + full[^HistoryDetailMaxChars..];
            }
            else
            {
                display = full;
            }

            var dialog = new ContentDialog
            {
                Title = _historyViewModel.DetailTitleText,
                Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = display,
                        FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                    },
                    MaxWidth = 560,
                    MaxHeight = 360,
                },
                CloseButtonText = _historyViewModel.CloseText,
                DefaultButton = ContentDialogButton.Close,
            };
            PrepareDialog(dialog);
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("HistoryDetail", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    // ==== Phase E：快捷键（补充文档 §五，加速器承载见 MainWindow.xaml） ====

    /// <summary>当前焦点是否在文本编辑控件内（Delete/Escape 等键不得打扰输入）。</summary>
    private bool IsTextEditingActive() =>
        FocusManager.GetFocusedElement(Content.XamlRoot) is TextBox;

    /// <summary>Ctrl+Shift+R：重新运行（评审修 4）——运行中 → Restart 编排（先停止，
    /// 等该运行终结后自动重跑）；否则直接运行。终结后 TryStart 失败（脚本被删等）
    /// 走 RunScriptAsync 既有错误通知路径。</summary>
    private async void OnShortcutRerun(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedScript is not { } script) return;

        if (_coordinator.IsRunning(script.Id))
        {
            // 停止并等终结；等待超时（false）不强重跑，停止编排的强制 Kill 仍兜底
            if (!await _coordinator.StopAndWaitExitAsync(script.Id)) return;
        }

        if (_dialogInFlight) return;
        _dialogInFlight = true;
        try
        {
            await RunScriptAsync(script);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("ShortcutRerun", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    /// <summary>Ctrl+E：编辑当前选中脚本。</summary>
    private async void OnShortcutEdit(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedScript is not { } script) return;
        if (_dialogInFlight) return;
        _dialogInFlight = true;
        try
        {
            await ShowScriptEditDialogAsync(script);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("ShortcutEdit", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    /// <summary>Delete：删除选中脚本（需确认；文本编辑态不触发）。</summary>
    private async void OnShortcutDelete(object sender, RoutedEventArgs e)
    {
        if (IsTextEditingActive()) return; // 文本框内 Delete = 删字符
        if (ViewModel.SelectedScript is not { } script) return;
        if (_dialogInFlight) return;
        _dialogInFlight = true;
        try
        {
            await DeleteScriptWithConfirmAsync(script);
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("ShortcutDelete", ex);
        }
        finally
        {
            _dialogInFlight = false;
        }
    }

    /// <summary>Ctrl+F：聚焦侧栏搜索框。</summary>
    private void OnShortcutFocusSearch(object sender, RoutedEventArgs e) => Sidebar.FocusSearchBox();

    /// <summary>Ctrl+Tab：循环切换终端标签。</summary>
    private void OnShortcutNextTab(object sender, RoutedEventArgs e)
    {
        if (Tabs.TabItems.Count <= 1) return;
        Tabs.SelectedIndex = (Tabs.SelectedIndex + 1) % Tabs.TabItems.Count;
    }

    /// <summary>Escape：搜索激活时清空搜索；弹窗打开时由 ContentDialog 原生关闭（本处不介入）。</summary>
    private void OnShortcutEscape(object sender, RoutedEventArgs e)
    {
        if (_dialogInFlight) return;
        Sidebar.ClearSearchIfActive();
    }

    /// <summary>async void 事件处理器兜底日志辅助（不得有未捕获异常经 async void 崩进程）。</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugWriteUnexpected(string context, Exception ex)
    {
#if DEBUG
        DebugLog.WriteLine($"{context}: 未预期异常（{ex}）");
#endif
    }

    /// <summary>DEBUG 日志辅助：Release 下调用点被编译器移除，避免 CS0168。</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugWriteError(string context, Exception ex)
    {
#if DEBUG
        DebugLog.WriteLine($"{context}（{ex.Message}）");
#endif
    }
}
