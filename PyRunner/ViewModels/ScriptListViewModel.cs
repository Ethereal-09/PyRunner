using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using PyRunner.Models;
using PyRunner.Services;

namespace PyRunner.ViewModels;

/// <summary>
/// 脚本列表项展示模型：包装 <see cref="Script"/> + 运行状态徽章数据。
/// 本阶段（Phase B）无运行功能，状态恒为 NotRun；Phase D 运行编排接管后
/// 由运行状态机更新 <see cref="Status"/> 与 <see cref="StatusText"/>。
/// </summary>
public sealed partial class ScriptListItemViewModel : ObservableObject
{
    public ScriptListItemViewModel(Script script, RunStatus status, string statusText)
    {
        Script = script;
        _status = status;
        _statusText = statusText;
    }

    public Script Script { get; }

    public string Name => Script.Name;
    public string FilePath => Script.FilePath;

    [ObservableProperty]
    private RunStatus _status;

    /// <summary>状态徽章文字（本地化；语言切换时由列表 VM 批量刷新）。</summary>
    [ObservableProperty]
    private string _statusText;

    /// <summary>右键菜单文字（本地化；随项重建/语言切换刷新）。</summary>
    public string MenuEditText { get; set; } = string.Empty;
    public string MenuDeleteText { get; set; } = string.Empty;
    public string MenuRunText { get; set; } = string.Empty;
    public string MenuScheduleText { get; set; } = string.Empty;

    /// <summary>右键「在记事本中打开」菜单文字（Phase E）。</summary>
    public string MenuNotebookText { get; set; } = string.Empty;

    /// <summary>右键收藏切换菜单文字（Phase E：按当前收藏态显示添加/取消）。</summary>
    public string MenuFavoriteText { get; set; } = string.Empty;
}

/// <summary>分类分组（CollectionViewSource IsSourceGrouped 数据源单元）。</summary>
public sealed class ScriptGroup
{
    public string Name { get; init; } = string.Empty;
    public ObservableCollection<ScriptListItemViewModel> Items { get; } = new();
    public string CountText => $"({Items.Count})";
}

/// <summary>
/// 侧栏脚本列表 ViewModel（Phase B）：
/// 全量缓存 + 内存搜索过滤（<see cref="IScriptService.Search"/>，名称/说明/标签不区分大小写包含匹配），
/// 搜索输入 250ms DispatcherQueueTimer 防抖；按分类分组；选中项供 Phase C/D 消费。
/// 数据层故障时仅列表不可用（<see cref="IsListAvailable"/>=false），不向上抛异常。
/// </summary>
public sealed partial class ScriptListViewModel : ObservableObject
{
    /// <summary>搜索防抖间隔（PRD：输入即实时过滤，250ms 防抖合并 DB 访问）。</summary>
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(250);

    private readonly IScriptService _scriptService;
    private readonly ILocalizationService _localization;
    private readonly DispatcherQueue _dispatcher;
    private DispatcherQueueTimer? _debounceTimer;

    private List<Script> _allScripts = new();

    /// <summary>运行状态字典（ScriptId → RunStatus）。Phase B 恒空（全部 NotRun），Phase D 由运行编排维护。
    /// 线程约定：仅 UI 线程可写；Phase D 后台更新须经 DispatcherQueue.TryEnqueue 回 UI 线程，
    /// 避免与 ApplyFilterAndGroup 的读取竞争。</summary>
    private readonly Dictionary<int, RunStatus> _statusById = new();

    public ScriptListViewModel(IScriptService scriptService, ILocalizationService localization)
    {
        _scriptService = scriptService;
        _localization = localization;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // 热切换订阅约定：语言变更时刷新展示文案（窗口关闭时经 DetachLocalization 退订）
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>窗口关闭时退订（服务层单例，避免事件持有瞬态 VM）。</summary>
    public void DetachLocalization()
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        _debounceTimer?.Stop();
    }

    // ---- 可观察状态 ----

    [ObservableProperty]
    private string _searchKeyword = string.Empty;

    /// <summary>是否搜索中（Phase C：侧栏在文件树与过滤列表两视图间切换）。</summary>
    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchKeyword);

    [ObservableProperty]
    private ScriptListItemViewModel? _selectedItem;

    /// <summary>数据层是否可用（GetAll 失败时 false：列表禁用，终端链路不受影响）。</summary>
    [ObservableProperty]
    private bool _isListAvailable = true;

    /// <summary>分类分组源（绑定 CollectionViewSource）。</summary>
    public ObservableCollection<ScriptGroup> Groups { get; } = new();

    /// <summary>当前选中脚本（Phase C/D 消费）。</summary>
    public Script? SelectedScript => SelectedItem?.Script;

    // ---- 展示属性（资源键） ----

    public string Title => _localization["Sidebar_Title"];
    public string ResourceManagerText => _localization["Sidebar_ResourceManager"];
    public string RefreshText => _localization["Sidebar_Refresh"];
    public string SettingsText => _localization["Settings_Title"];
    public string SearchPlaceholder => _localization["Sidebar_SearchPlaceholder"];
    public string EmptyText => _localization["Sidebar_Empty_Text"];
    public string OpenSettingsText => _localization["Button_OpenSettings"];
    public string NoResultText => _localization["Sidebar_NoResult_Text"];
    public string NoResultClearText => _localization["Sidebar_NoResult_Clear"];
    public string UnavailableText => _localization["Sidebar_Unavailable"];

    /// <summary>空状态（无任何脚本登记）。</summary>
    public bool ShowEmptyState => IsListAvailable && _allScripts.Count == 0;

    /// <summary>搜索无结果（有脚本但过滤后为空）。</summary>
    public bool ShowNoResult => IsListAvailable && _allScripts.Count > 0 &&
        Groups.Sum(g => g.Items.Count) == 0;

    // ---- 数据加载 ----

    /// <summary>从数据层全量加载并重建分组；数据层故障时仅列表不可用。</summary>
    public void Load(string? selectFilePath = null)
    {
        try
        {
            _allScripts = _scriptService.GetAll().ToList();
            IsListAvailable = true;

            // 评审修复 7：按当前登记 Id 集合修剪状态字典，已删除脚本的历史
            // 徽章状态不残留（否则字典只增不减）
            if (_statusById.Count > 0)
            {
                foreach (var staleId in _statusById.Keys.Where(id => !_allScripts.Any(s => s.Id == id)).ToList())
                    _statusById.Remove(staleId);
            }
        }
        catch (Exception ex)
        {
            IsListAvailable = false;
            Groups.Clear();
            SelectedItem = null; // 防陈旧选中：数据层不可用后 SelectedScript 必须失效
            DebugWriteError("ScriptList: 脚本列表加载失败，列表已禁用", ex);
            RaiseVisibilityChanged();
            return;
        }

        ApplyFilterAndGroup(selectFilePath);
    }

    // ---- 搜索防抖 ----

    partial void OnSearchKeywordChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearchActive));
        var timer = _debounceTimer ??= _dispatcher.CreateTimer();
        timer.Stop();
        timer.Interval = DebounceInterval;
        timer.Tick -= OnDebounceTick;
        timer.Tick += OnDebounceTick;
        timer.Start();
    }

    private void OnDebounceTick(DispatcherQueueTimer timer, object e)
    {
        timer.Stop();
        ApplyFilterAndGroup();
    }

    /// <summary>清除搜索（搜索无结果空状态的引导操作）。</summary>
    public void ClearSearch() => SearchKeyword = string.Empty;

    /// <summary>
    /// 按脚本 Id 选中侧栏项。终端标签切换时调用；若目标被搜索过滤隐藏，立即退出搜索并重建分组，
    /// 保证标题脚本信息、侧栏选择和当前终端保持同一个上下文。
    /// </summary>
    public bool SelectByScriptId(int scriptId)
    {
        var item = FindItemById(scriptId);
        if (item == null && IsSearchActive)
        {
            _debounceTimer?.Stop();
            SearchKeyword = string.Empty;
            _debounceTimer?.Stop();
            ApplyFilterAndGroup();
            item = FindItemById(scriptId);
        }

        if (item == null) return false;
        SelectedItem = item;
        return true;
    }

    // ---- 过滤与分组 ----

    private void ApplyFilterAndGroup(string? selectFilePath = null)
    {
        var keyword = SearchKeyword.Trim();

        IReadOnlyList<Script> filtered;
        try
        {
            // Search：空关键词 = 全量；否则名称/说明/标签 OrdinalIgnoreCase 包含匹配（含中文与大小写变体）
            filtered = keyword.Length == 0 ? _allScripts : _scriptService.Search(keyword);
        }
        catch (Exception ex)
        {
            // 过滤故障退化为全量展示，避免列表整体不可用
            filtered = _allScripts;
            DebugWriteError("ScriptList: 搜索过滤失败，退化为全量列表", ex);
        }

        var selectedId = SelectedItem?.Script.Id;

        Groups.Clear();
        foreach (var group in filtered
                     .GroupBy(s => CategoryDisplayName(s.Category))
                     .OrderBy(g => g.Key, StringComparer.CurrentCulture))
        {
            var scriptGroup = new ScriptGroup { Name = group.Key };
            foreach (var script in group)
            {
                var status = GetRunStatus(script.Id);
                scriptGroup.Items.Add(new ScriptListItemViewModel(script, status, StatusText(status))
                {
                    MenuEditText = _localization["Menu_Edit"],
                    MenuDeleteText = _localization["Menu_Delete"],
                    MenuRunText = _localization["Menu_Run"],
                    MenuScheduleText = _localization["Schedule_CreateForScript"],
                    MenuNotebookText = _localization["Menu_OpenInNotepad"],
                    MenuFavoriteText = script.IsFavorite
                        ? _localization["Menu_RemoveFavorite"]
                        : _localization["Menu_AddFavorite"],
                });
            }
            Groups.Add(scriptGroup);
        }

        // 保持/恢复选中：优先保存返回的文件路径，其次原选中 Id
        SelectedItem = FindItemByFilePath(selectFilePath)
            ?? FindItemById(selectedId)
            ?? Groups.SelectMany(group => group.Items).FirstOrDefault();

        RaiseVisibilityChanged();
    }

    private ScriptListItemViewModel? FindItemByFilePath(string? filePath) =>
        filePath == null
            ? null
            : Groups.SelectMany(g => g.Items)
                .FirstOrDefault(i => string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

    private ScriptListItemViewModel? FindItemById(int? id) =>
        id == null
            ? null
            : Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Script.Id == id);

    private string CategoryDisplayName(string? category) =>
        string.IsNullOrWhiteSpace(category)
            ? _localization["Category_Uncategorized"]
            : category.Trim();

    // ---- 运行状态（Phase D 接管） ----

    /// <summary>查询脚本运行状态；无记录 = NotRun。</summary>
    public RunStatus GetRunStatus(int scriptId) =>
        _statusById.TryGetValue(scriptId, out var status) ? status : RunStatus.NotRun;

    /// <summary>运行编排更新状态（仅 UI 线程）：写状态字典并同步已展示项的徽章。</summary>
    public void SetRunStatus(int scriptId, RunStatus status)
    {
        _statusById[scriptId] = status;
        foreach (var item in Groups.SelectMany(g => g.Items))
        {
            if (item.Script.Id == scriptId)
            {
                item.Status = status;
                item.StatusText = StatusText(status);
            }
        }
    }

    private string StatusText(RunStatus status) => status switch
    {
        RunStatus.Running => _localization["StatusBadge_Running"],
        RunStatus.Success => _localization["StatusBadge_Success"],
        RunStatus.Failed => _localization["StatusBadge_Failed"],
        RunStatus.Killed => _localization["StatusBadge_Killed"],
        _ => _localization["StatusBadge_NotRun"],
    };

    // ---- 语言热切换 ----

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ResourceManagerText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(SettingsText));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(OpenSettingsText));
        OnPropertyChanged(nameof(NoResultText));
        OnPropertyChanged(nameof(NoResultClearText));
        OnPropertyChanged(nameof(UnavailableText));

        // 「未分类」分组名与项内本地化文字需重建（保持选中与关键词）
        ApplyFilterAndGroup(SelectedItem?.FilePath);
    }

    private void RaiseVisibilityChanged()
    {
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowNoResult));
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
