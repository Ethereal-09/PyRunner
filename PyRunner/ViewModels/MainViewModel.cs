using CommunityToolkit.Mvvm.ComponentModel;
using PyRunner.Models;
using PyRunner.Services;

namespace PyRunner.ViewModels;

/// <summary>
/// 壳视图模型（Phase D 交付 12）：MainViewModel 自 M0 PoC 的「全链路托管者」退化为
/// 窗口壳 —— 承载状态栏文案、选中脚本与运行协调入口状态机。
/// WebView2 ⇄ ConPTY 桥接逻辑已逐行原样迁移到
/// <see cref="TerminalSessionViewModel"/>（每标签一个实例）；运行编排实体见
/// Services\RunCoordinator（由 MainWindow 接线）。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    /// <summary>当前状态栏文案的资源键与格式参数：语言热切换时据此重算 Status，
    /// 避免状态栏停留在切换前的旧语言文案（仅 UI 线程读写）。</summary>
    private string _statusKey = "Status_Initializing";
    private object[]? _statusArgs;

    public MainViewModel(ILocalizationService localization)
    {
        _localization = localization;
        // 语言热切换：刷新当前状态栏文案；Shutdown 时退订（服务为单例，避免持有瞬态 VM）
        _localization.LanguageChanged += OnLanguageChanged;
        SetLocalizedStatus("Status_Initializing");
    }

    [ObservableProperty]
    private string _status = string.Empty;

    // ---- 运行协调入口状态机（真相源：RunCoordinator 经 MainWindow 广播） ----

    /// <summary>当前选中脚本（侧栏列表/文件树选择同步）。</summary>
    [ObservableProperty]
    private Script? _selectedScript;

    /// <summary>选中脚本是否运行中（运行按钮禁用+loading、停止按钮高亮的判据）。</summary>
    [ObservableProperty]
    private bool _selectedScriptRunning;

    /// <summary>运行按钮可用性：有选中脚本且未在运行。</summary>
    public bool CanRun => SelectedScript != null && !SelectedScriptRunning;

    /// <summary>停止按钮可用性：选中脚本运行中。</summary>
    public bool CanStop => SelectedScriptRunning;

    public string RunText => _localization["Button_Run"];
    public string StopText => _localization["Button_Stop"];

    /// <summary>空工作区提示（无任何标签时居中展示）。</summary>
    public string WorkspaceHintText => SelectedScript == null
        ? _localization["Workspace_Hint"]
        : _localization["Workspace_SelectedHint"];

    partial void OnSelectedScriptChanged(Script? value)
    {
        RaiseRunState();
        OnPropertyChanged(nameof(WorkspaceHintText));
    }
    partial void OnSelectedScriptRunningChanged(bool value) => RaiseRunState();

    private void RaiseRunState()
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanStop));
    }

    /// <summary>设置状态栏文案（记录资源键，供语言切换重算）。</summary>
    public void SetLocalizedStatus(string key, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args.Length == 0 ? null : args;
        Status = args.Length == 0
            ? _localization[key]
            : string.Format(_localization[key], args);
    }

    private void OnLanguageChanged()
    {
        SetLocalizedStatus(_statusKey, _statusArgs ?? Array.Empty<object>());
        OnPropertyChanged(nameof(RunText));
        OnPropertyChanged(nameof(StopText));
        OnPropertyChanged(nameof(WorkspaceHintText));
    }

    /// <summary>窗口关闭时退订单例事件（会话释放归各标签 TerminalSessionViewModel.Shutdown）。</summary>
    public void Shutdown()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}
