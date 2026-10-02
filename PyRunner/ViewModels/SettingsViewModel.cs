using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PyRunner.Models;
using PyRunner.Services;

namespace PyRunner.ViewModels;

/// <summary>设置弹窗行模型：脚本路径 + 实时有效性（Directory.Exists）。</summary>
public sealed class ScriptPathRow
{
    public int Id { get; init; }
    public string Path { get; init; } = string.Empty;
    public bool IsValid { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string RemoveText { get; init; } = string.Empty;
}

/// <summary>设置弹窗行模型：解释器 + 版本检测状态。</summary>
public sealed partial class InterpreterRow : ObservableObject
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string? Version { get; init; }
    public string RemoveText { get; init; } = string.Empty;
    // 延后项留痕：默认解释器的 IsDefault UI 呈现（星标/置顶等）登记至 Phase D 任务备忘，本阶段仅透传数据。
    public bool IsDefault { get; init; }

    /// <summary>状态文字（本地化「已连接」；构造时由 VM 填充，语言切换随列表重建刷新）。</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>版本列显示：优先真实版本号，未检测到时显示登记名。</summary>
    public string VersionDisplay => string.IsNullOrWhiteSpace(Version) ? Name : Version!;
}

/// <summary>语言下拉选项。</summary>
public sealed class LanguageOption
{
    public string Code { get; init; } = string.Empty;
    public string Display { get; init; } = string.Empty;
    public override string ToString() => Display;
}

/// <summary>
/// 设置弹窗 ViewModel（Phase C 交付 9）：四分组——脚本路径 / 解释器 / 运行选项 / 语言。
/// 路径与解释器操作即时生效；运行选项、终端字号和语言经确定按钮提交草稿。
/// 解释器版本检测为同步阻塞（exe --version，3s 超时），由对话框侧 Task.Run 包装为异步。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IScriptPathService _scriptPathService;
    private readonly IScriptService _scriptService;
    private readonly IInterpreterService _interpreterService;
    private readonly IStartupService _startupService;
    private readonly ILocalizationService _localization;
    private readonly ITerminalAppearanceService _terminalAppearance;

    public SettingsViewModel(
        ISettingsService settingsService,
        IScriptPathService scriptPathService,
        IScriptService scriptService,
        IInterpreterService interpreterService,
        IStartupService startupService,
        ILocalizationService localization,
        ITerminalAppearanceService terminalAppearance)
    {
        _settingsService = settingsService;
        _scriptPathService = scriptPathService;
        _scriptService = scriptService;
        _interpreterService = interpreterService;
        _startupService = startupService;
        _localization = localization;
        _terminalAppearance = terminalAppearance;
        _terminalFontSizeDraft = terminalAppearance.FontSize;

        var snapshot = settingsService.Current;
        _runOptionsDraft = snapshot;

        _localization.LanguageChanged += OnLanguageChanged;
        RefreshLists();
    }

    /// <summary>对话框关闭时退订（服务层单例，避免事件持有瞬态 VM）。</summary>
    public void DetachLocalization()
    {
        _detached = true; // 关闭防护：后台版本检测续延回来时不再触碰 UI 集合
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>对话框已关闭/已 Detach 标志（评审修复）：AddInterpreterAsync 的后台检测续延
    /// 可能在对话框关闭后才回到 UI 线程，此时 RefreshLists 会触碰已 Detach 的瞬态 VM。
    /// 完整 CTS 取消链路（中断 exe --version 进程）代价较大，延后实施；
    /// 当前仅保证续延开头检查跳过 UI 刷新，DB 写入本身幂等无害。</summary>
    private bool _detached;
    private readonly AppSettings _runOptionsDraft;
    public string ImmediateChangesText => _localization["Settings_ImmediateChanges"];
    public string DraftChangesText => _localization["Settings_DraftChanges"];
    public string PathsNavText => _localization["Settings_Nav_Paths"];
    public string InterpretersNavText => _localization["Settings_Nav_Interpreters"];
    public string LanguageNavText => _localization["Settings_Nav_Language"];
    public string RemovePathText => _localization["Settings_RemovePath"];
    public string RemoveInterpreterText => _localization["Settings_RemoveInterpreter"];

    public bool CommitRunOptions()
    {
        try
        {
            if (_settingsService.Current.AutoStart != AutoStart) _startupService.SetEnabled(AutoStart);
            _settingsService.Update(settings =>
            {
                settings.TerminalAutoClear = TerminalAutoClear;
                settings.NotifyOnFail = NotifyOnFail;
                settings.LiveFlush = LiveFlush;
                settings.AutoStart = AutoStart;
                settings.AutoRefreshScripts = AutoRefreshScripts;
            });
            if (SelectedLanguage is not null) _localization.SetLanguage(SelectedLanguage.Code);
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = _localization["Error_AutoStartFailed"];
            DebugWriteError("Settings: 保存运行设置失败", ex);
            return false;
        }
    }

    // ---- 集合 ----

    public ObservableCollection<ScriptPathRow> ScriptPaths { get; } = new();
    public ObservableCollection<InterpreterRow> Interpreters { get; } = new();

    /// <summary>行内错误提示（非空时红字展示）。</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>解释器添加中（异步版本检测，行内 ProgressRing 不阻塞 UI）。</summary>
    [ObservableProperty]
    private bool _isAddingInterpreter;

    // ---- 展示属性（资源键） ----

    public string Title => _localization["Settings_Title"];
    public string GroupScriptPaths => _localization["Settings_ScriptPaths"];
    public string AddFolderText => _localization["Settings_AddFolder"];
    public string GroupInterpreters => _localization["Settings_Interpreters"];
    public string AddInterpreterText => _localization["Settings_AddInterpreter"];
    public string GroupRunOptions => _localization["Settings_RunOptions"];
    public string RunAndTerminalText => _localization["Settings_RunAndTerminal"];
    public string OptAutoClearText => _localization["Settings_Opt_AutoClear"];
    public string OptNotifyOnFailText => _localization["Settings_Opt_NotifyOnFail"];
    public string OptLiveFlushText => _localization["Settings_Opt_LiveFlush"];
    public string OptAutoRefreshScriptsText => _localization["Settings_Opt_AutoRefreshScripts"];
    public string OptAutoStartText => _localization["Settings_Opt_AutoStart"];
    public string GroupLanguage => _localization["Settings_Language"];
    public string GroupTerminalAppearance => _localization["Settings_TerminalAppearance"];
    public string TerminalFontSizeText => _localization["Settings_TerminalFontSize"];
    public string TerminalPreviewText => _localization["Settings_TerminalPreview"];
    public string OkText => _localization["Button_OK"];
    public string CancelText => _localization["Button_Cancel"];
    public string ConnectedText => _localization["Settings_Connected"];
    public event Action<int>? TerminalFontSizePreviewChanged;

    [ObservableProperty]
    private double _terminalFontSizeDraft;

    partial void OnTerminalFontSizeDraftChanged(double value)
    {
        var clamped = Math.Clamp((int)Math.Round(value),
            TerminalAppearanceService.MinimumFontSize, TerminalAppearanceService.MaximumFontSize);
        if (Math.Abs(value - clamped) > 0.001)
        {
            TerminalFontSizeDraft = clamped;
            return;
        }
        TerminalFontSizePreviewChanged?.Invoke(clamped);
    }

    public void CommitTerminalAppearance() =>
        _terminalAppearance.SetFontSize((int)Math.Round(TerminalFontSizeDraft));

    public void CancelTerminalAppearance() =>
        TerminalFontSizeDraft = _terminalAppearance.FontSize;

    // ---- 运行选项（勾选即持久化） ----

    public bool TerminalAutoClear
    {
        get => _runOptionsDraft.TerminalAutoClear;
        set { _runOptionsDraft.TerminalAutoClear = value; OnPropertyChanged(); }
    }

    public bool NotifyOnFail
    {
        get => _runOptionsDraft.NotifyOnFail;
        set { _runOptionsDraft.NotifyOnFail = value; OnPropertyChanged(); }
    }

    public bool LiveFlush
    {
        get => _runOptionsDraft.LiveFlush;
        set { _runOptionsDraft.LiveFlush = value; OnPropertyChanged(); }
    }

    public bool AutoStart
    {
        get => _runOptionsDraft.AutoStart;
        set
        {
            _runOptionsDraft.AutoStart = value;
            OnPropertyChanged();
        }
    }

    public bool AutoRefreshScripts
    {
        get => _runOptionsDraft.AutoRefreshScripts;
        set { _runOptionsDraft.AutoRefreshScripts = value; OnPropertyChanged(); }
    }

    // ---- 语言 ----

    public List<LanguageOption> Languages { get; } = new();

    [ObservableProperty]
    private LanguageOption? _selectedLanguage;

    /// <summary>重建语言下拉（本地化显示名随语言切换刷新；选中项跟随 CurrentLanguage）。</summary>
    private void RefreshLanguageOptions()
    {
        Languages.Clear();
        Languages.Add(new LanguageOption { Code = "zh-CN", Display = _localization["Settings_Language_Zh"] });
        Languages.Add(new LanguageOption { Code = "en-US", Display = _localization["Settings_Language_En"] });
        SelectedLanguage = Languages.FirstOrDefault(l =>
            string.Equals(l.Code, _localization.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            ?? Languages[0];
        OnPropertyChanged(nameof(Languages));
    }

    /// <summary>下拉变更：即时生效（LocalizationService 内部写回 settings）。</summary>
    public void ApplyLanguageSelection()
    {
        if (SelectedLanguage == null) return;
        // 语言与运行选项一起在确定时提交，取消不改变应用语言。
    }

    // ---- 分组一：脚本路径 ----

    /// <summary>添加脚本目录后立即同步全部已选目录；环境和依赖目录不会导入。</summary>
    public async Task AddScriptPathAsync(string path)
    {
        ErrorMessage = string.Empty;
        try
        {
            var addedPath = _scriptPathService.Add(path);
            var configuredPaths = _scriptPathService.GetAll()
                .Where(item => item.Enabled)
                .Select(item => item.Path)
                .ToList();
            await Task.Run(() => _scriptService.ImportFromPaths(configuredPaths));
            if (_detached) return;
            RefreshLists();
        }
        catch (ValidationException ex)
        {
            ErrorMessage = _localization[ex.LocalizationKey];
        }
        catch (Exception ex)
        {
            ErrorMessage = _localization["Error_SaveFailed"];
            DebugWriteError("Settings: 添加脚本路径失败", ex);
        }
    }

    public void RemoveScriptPath(ScriptPathRow row)
    {
        ErrorMessage = string.Empty;
        try
        {
            _scriptPathService.Remove(row.Id);
            RefreshLists();
        }
        catch (Exception ex)
        {
            ErrorMessage = _localization["Error_SaveFailed"];
            DebugWriteError("Settings: 移除脚本路径失败", ex);
        }
    }

    // ---- 分组二：解释器 ----

    /// <summary>添加解释器：同步版本检测包在 Task.Run，行内 ProgressRing 指示（调用方 await）。</summary>
    public async Task AddInterpreterAsync(string exePath)
    {
        ErrorMessage = string.Empty;
        IsAddingInterpreter = true;
        try
        {
            // AddFromPath 内部同步执行 exe --version（3s 超时），放后台线程避免卡 UI
            await Task.Run(() => _interpreterService.AddFromPath(exePath));
            if (_detached) return; // 对话框已关闭：DB 已写入，仅跳过 UI 刷新（见字段注释）
            RefreshLists();
        }
        catch (ValidationException ex)
        {
            ErrorMessage = _localization[ex.LocalizationKey];
        }
        catch (Exception ex)
        {
            ErrorMessage = _localization["Error_SaveFailed"];
            DebugWriteError("Settings: 添加解释器失败", ex);
        }
        finally
        {
            IsAddingInterpreter = false;
        }
    }

    public void RemoveInterpreter(InterpreterRow row)
    {
        ErrorMessage = string.Empty;
        try
        {
            _interpreterService.Delete(row.Id);
            RefreshLists();
        }
        catch (ValidationException ex)
        {
            // 引用保护：仍被脚本登记的记录不可删（PRD §8.2）；
            // 服务层抛 ValidationException（非 InvalidOperationException），文案走其本地化键
            ErrorMessage = _localization[ex.LocalizationKey];
        }
        catch (Exception ex)
        {
            ErrorMessage = _localization["Error_SaveFailed"];
            DebugWriteError("Settings: 移除解释器失败", ex);
        }
    }

    // ---- 数据刷新 ----

    public void RefreshLists()
    {
        ScriptPaths.Clear();
        try
        {
            foreach (var sp in _scriptPathService.GetAll())
            {
                var valid = Directory.Exists(sp.Path);
                ScriptPaths.Add(new ScriptPathRow
                {
                    Id = sp.Id,
                    Path = sp.Path,
                    IsValid = valid,
                    StatusText = valid ? _localization["Settings_Valid"] : _localization["Settings_Invalid"],
                    RemoveText = RemovePathText,
                });
            }
        }
        catch (Exception ex)
        {
            DebugWriteError("Settings: 脚本路径列表加载失败", ex);
        }

        Interpreters.Clear();
        try
        {
            foreach (var i in _interpreterService.GetAll())
            {
                Interpreters.Add(new InterpreterRow
                {
                    Id = i.Id,
                    Name = i.Name,
                    ExecutablePath = i.ExecutablePath,
                    Version = i.Version,
                    IsDefault = i.IsDefault,
                    StatusText = _localization["Settings_Connected"],
                    RemoveText = RemoveInterpreterText,
                });
            }
        }
        catch (Exception ex)
        {
            DebugWriteError("Settings: 解释器列表加载失败", ex);
        }

        RefreshLanguageOptions();
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(GroupScriptPaths));
        OnPropertyChanged(nameof(AddFolderText));
        OnPropertyChanged(nameof(GroupInterpreters));
        OnPropertyChanged(nameof(AddInterpreterText));
        OnPropertyChanged(nameof(GroupRunOptions));
        OnPropertyChanged(nameof(RunAndTerminalText));
        OnPropertyChanged(nameof(OptAutoClearText));
        OnPropertyChanged(nameof(OptNotifyOnFailText));
        OnPropertyChanged(nameof(OptLiveFlushText));
        OnPropertyChanged(nameof(OptAutoRefreshScriptsText));
        OnPropertyChanged(nameof(OptAutoStartText));
        OnPropertyChanged(nameof(GroupLanguage));
        OnPropertyChanged(nameof(GroupTerminalAppearance));
        OnPropertyChanged(nameof(TerminalFontSizeText));
        OnPropertyChanged(nameof(TerminalPreviewText));
        OnPropertyChanged(nameof(OkText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(ConnectedText));
        RefreshLists(); // 行内状态文案（Valid/Invalid）本地化重建
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugWriteError(string context, Exception ex)
    {
#if DEBUG
        DebugLog.WriteLine($"{context}（{ex.Message}）");
#endif
    }
}
