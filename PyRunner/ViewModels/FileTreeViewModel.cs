using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using PyRunner.Models;
using PyRunner.Services;
// 注：本 VM 持有 TreeViewNode（WinUI 3 TreeView 的必需载体），
// 属于实用取舍：树节点生命周期与 VM 重建同步，不跨窗口复用。

namespace PyRunner.ViewModels;

/// <summary>侧栏文件树节点种类。</summary>
public enum FileTreeNodeKind
{
    /// <summary>懒加载占位子节点（仅用于显示展开箭头，展开时替换为真实内容）。</summary>
    Placeholder,
    AllScripts,
    Favorites,
    Categories,
    Category,
    ScriptRoot,
    Directory,
    PyFile,
}

/// <summary>
/// 文件树节点展示模型。TreeViewNode 为 WinUI 3 TreeView 的必需载体，
/// 由 VM 持有并在 Load 时整体重建（数据变更后全量重建优于增量同步）。
/// </summary>
public sealed partial class FileTreeNodeViewModel : ObservableObject
{
    public FileTreeNodeViewModel(FileTreeNodeKind kind, string name, string fullPath = "")
    {
        Kind = kind;
        Name = name;
        FullPath = fullPath;
        Node = new TreeViewNode { Content = this };
    }

    public FileTreeNodeKind Kind { get; }
    public string Name { get; }
    public string FullPath { get; }

    /// <summary>Segoe MDL2 Assets 图标字形。直接绑定 FontIcon，避免 TreeViewNode
    /// 作为 ContentControl 内容时模板选择失败并把节点名称裁成首字符显示。</summary>
    public string IconGlyph => Kind switch
    {
        FileTreeNodeKind.Favorites => "\uE734",            // FavoriteStar
        FileTreeNodeKind.AllScripts or FileTreeNodeKind.PyFile => "\uE8A5", // Document
        FileTreeNodeKind.Categories or FileTreeNodeKind.Category => "\uE8EC", // Tag
        FileTreeNodeKind.Placeholder => string.Empty,
        _ => "\uE8B7",                                    // Folder
    };

    /// <summary>UIA 无障碍名来源：TreeViewItem 对无 AutomationProperties.Name 的项
    /// 以 Content.ToString() 暴露 Name，否则自动化拿到类型全名。</summary>
    public override string ToString() => Name;

    /// <summary>数量徽章文字，如 "6"；空串不显示（仅入树前赋值，无需可观察）。</summary>
    public string BadgeText { get; set; } = string.Empty;

    /// <summary>右键「运行」菜单文字（仅已登记 .py 节点注入，其余空串）。</summary>
    public string MenuRunText { get; set; } = string.Empty;
    public string MenuScheduleText { get; set; } = string.Empty;

    /// <summary>右键「在记事本中打开」菜单文字（Phase E；仅 .py 节点注入）。</summary>
    public string MenuNotebookText { get; set; } = string.Empty;

    /// <summary>右键收藏切换菜单文字（Phase E；仅已登记 .py 注入，
    /// 按当前收藏态显示「添加收藏」/「取消收藏」）。</summary>
    public string MenuFavoriteText { get; set; } = string.Empty;

    /// <summary>是否已登记脚本（收藏切换入口的判据）。</summary>
    public bool IsRegistered { get; set; }

    /// <summary>收藏态（供星标图标/收藏节点构建）。</summary>
    public bool IsFavorite { get; set; }

    /// <summary>失效节点（路径不存在）：置灰 + 删除线。
    /// 可观察：展示期间目录被删除时懒加载置位，绑定（前景/删除线）即时重求值。</summary>
    [ObservableProperty]
    private bool _isInvalid;

    /// <summary>子节点是否已从磁盘枚举（懒加载标志）。</summary>
    public bool ChildrenLoaded { get; set; }

    public TreeViewNode Node { get; }

    /// <summary>展开状态持久化键：虚拟节点带前缀，目录节点为绝对路径。</summary>
    public string ExpansionKey => Kind switch
    {
        FileTreeNodeKind.Categories => "v:categories",
        FileTreeNodeKind.Category => "c:" + Name,
        FileTreeNodeKind.ScriptRoot or FileTreeNodeKind.Directory => FullPath,
        _ => string.Empty,
    };
}

/// <summary>
/// 侧栏文件树 ViewModel（Phase C 交付 8，Phase E 交付 15 增强）：
/// 两段式 TreeView —— 顶部虚拟节点（所有脚本/收藏/分类 + 数量徽章；收藏节点消费
/// Script.IsFavorite 填充子级），下方真实目录树（ScriptPath 根，展开时才枚举子目录，
/// 只显示目录与 .py 文件；目录枚举在后台线程，结果回 UI 线程装配，UNC/失效盘不阻塞 UI）。
/// 展开状态持久化到 AppSettings.SavedTreeExpanded（JSON 字符串数组）。
/// 数据层故障仅降级为空树（IsTreeDegraded 驱动视图侧降级横幅），不向上抛异常。
/// 磁盘监听由 ScriptDirectoryWatcher 驱动（变更后经 MainWindow 调 Load 重建；
/// 侧栏刷新按钮为手动兜底）。
/// </summary>
public sealed partial class FileTreeViewModel : ObservableObject
{
    private readonly IScriptService _scriptService;
    private readonly IScriptPathService _scriptPathService;
    private readonly IInterpreterService _interpreterService;
    private readonly ILocalizationService _localization;
    private readonly ISettingsService _settingsService;

    /// <summary>已登记脚本的路径索引（OrdinalIgnoreCase），供 .py 节点点击判定。</summary>
    private readonly Dictionary<string, Script> _scriptsByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前展开节点键集合（与 settings 双向同步）。</summary>
    private readonly HashSet<string> _expandedKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Collapsed 事件抑制集合（评审修 1：按节点判定）。懒加载装配子级期间
    /// TreeView 会反弹 Collapsed（实证），该反弹不得从持久化集合移除键；
    /// 仅在「Children.Clear + 子级装配」的同步段内把当事节点加入集合，
    /// 后台枚举 await 窗口（秒级）不维持抑制——期间用户折叠其他节点照常生效。</summary>
    private readonly HashSet<TreeViewNode> _suppressCollapseFor = new();

    public FileTreeViewModel(
        IScriptService scriptService,
        IScriptPathService scriptPathService,
        IInterpreterService interpreterService,
        ILocalizationService localization,
        ISettingsService settingsService)
    {
        _scriptService = scriptService;
        _scriptPathService = scriptPathService;
        _interpreterService = interpreterService;
        _localization = localization;
        _settingsService = settingsService;

        _localization.LanguageChanged += OnLanguageChanged;
        LoadExpansionState();
    }

    /// <summary>窗口关闭时退订（服务层单例，避免事件持有瞬态 VM）。</summary>
    public void DetachLocalization() => _localization.LanguageChanged -= OnLanguageChanged;

    // ---- 事件：由 MainWindow 协调（列表选中 / 登记对话框），避免 VM 间直接依赖 ----

    /// <summary>点击的 .py 已登记：参数为文件绝对路径（窗口据此选中列表项）。</summary>
    public event Action<string>? RegisteredScriptClicked;

    /// <summary>点击的 .py 未登记：参数为文件绝对路径（窗口据此打开预填新增对话框）。</summary>
    public event Action<string>? UnregisteredScriptClicked;

    /// <summary>右键「运行」已登记脚本：参数为脚本记录（窗口据此接 RunCoordinator）。</summary>
    public event Action<Script>? ScriptRunRequested;

    /// <summary>右键「在记事本中打开」（Phase E）：参数为文件绝对路径（窗口校验存在性后拉起 notepad）。</summary>
    public event Action<string>? ScriptNotebookRequested;

    /// <summary>右键收藏切换（Phase E）：参数为脚本记录（窗口据此 ToggleFavorite 并刷新）。</summary>
    public event Action<Script>? FavoriteToggleRequested;

    // ---- 可观察状态 ----

    /// <summary>TreeView 根节点集合（视图侧镜像到 TreeView.RootNodes）。</summary>
    public ObservableCollection<TreeViewNode> RootNodes { get; } = new();

    /// <summary>底部「已连接解释器」区数据（InterpreterService）。</summary>
    public ObservableCollection<Interpreter> Interpreters { get; } = new();

    /// <summary>是否尚未配置任何脚本目录（空状态提示）。</summary>
    [ObservableProperty]
    private bool _hasScriptPaths;

    /// <summary>数据层降级标志：Load 时脚本/路径读取失败（DB 损坏/锁死）置 true，
    /// 视图侧据此展示降级横幅（与搜索状态无关），避免徽章计 0 的静默假象。</summary>
    [ObservableProperty]
    private bool _isTreeDegraded;

    /// <summary>树重建中（手动刷新/监听重建时驱动侧栏 ProgressRing 加载态，Phase E）。</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>解释器区是否为空。</summary>
    public bool ShowNoInterpreter => Interpreters.Count == 0;

    // ---- 展示属性（资源键） ----

    public string AllScriptsText => _localization["Sidebar_TreeAllScripts"];
    public string FavoritesText => _localization["Sidebar_TreeFavorites"];
    public string CategoriesText => _localization["Sidebar_TreeCategories"];
    public string NoScriptPathsText => _localization["Sidebar_NoScriptPaths"];
    public string InterpreterTitle => _localization["Sidebar_Interpreters"];
    public string NoInterpreterText => _localization["Sidebar_NoInterpreter"];

    /// <summary>树节点右键「运行」菜单文字（PyFile 节点注入用）。</summary>
    public string MenuRunText => _localization["Menu_Run"];
    public string MenuScheduleText => _localization["Schedule_CreateForScript"];

    /// <summary>树节点右键「在记事本中打开」菜单文字（Phase E）。</summary>
    public string MenuNotebookText => _localization["Menu_OpenInNotepad"];

    /// <summary>添加收藏菜单文字（Phase E）。</summary>
    public string MenuAddFavoriteText => _localization["Menu_AddFavorite"];

    /// <summary>取消收藏菜单文字（Phase E）。</summary>
    public string MenuRemoveFavoriteText => _localization["Menu_RemoveFavorite"];

    /// <summary>收藏为空时收藏虚拟节点的占位文字（Phase E）。</summary>
    public string NoFavoritesText => _localization["Sidebar_NoFavorites"];

    /// <summary>扫描加载态文案（Phase E：「正在扫描…」）。</summary>
    public string LoadingText => _localization["Sidebar_Loading"];

    /// <summary>无脚本路径空状态的引导按钮文字（Phase E：「打开设置」）。</summary>
    public string OpenSettingsText => _localization["Button_OpenSettings"];

    /// <summary>降级横幅文案（复用列表侧 Sidebar_Unavailable 键）。</summary>
    public string TreeDegradedText => _localization["Sidebar_Unavailable"];

    /// <summary>全量重建：虚拟节点（徽章计数）+ ScriptPath 目录根 + 失效标记。</summary>
    public void Load()
    {
        var degraded = false;

        List<Script> scripts = new();
        try
        {
            scripts = _scriptService.GetAll().ToList();
        }
        catch (Exception ex)
        {
            degraded = true;
            DebugWriteError("FileTree: 脚本登记加载失败，徽章计 0", ex);
        }

        _scriptsByPath.Clear();
        foreach (var s in scripts)
            _scriptsByPath[s.FilePath] = s;

        List<ScriptPath> paths = new();
        try
        {
            paths = _scriptPathService.GetAll().ToList();
        }
        catch (Exception ex)
        {
            degraded = true;
            DebugWriteError("FileTree: 脚本路径加载失败，目录树为空", ex);
        }

        IsTreeDegraded = degraded;

        RootNodes.Clear();
#if DEBUG
        DebugLog.WriteLine("FileTree: Load() 开始重建 RootNodes");
#endif

        // -- 顶部虚拟节点 --
        var all = new FileTreeNodeViewModel(FileTreeNodeKind.AllScripts, AllScriptsText)
        {
            BadgeText = Badge(scripts.Count),
        };
        var fav = new FileTreeNodeViewModel(FileTreeNodeKind.Favorites, FavoritesText)
        {
            BadgeText = Badge(scripts.Count(s => s.IsFavorite)),
        };
        // Phase E：收藏虚拟节点消费 IsFavorite —— 子级 = 全部已收藏脚本（点击=已登记选中，
        // 右键=运行/记事本/取消收藏）；零收藏时放占位文字节点（空状态第 7 类）
        var favorites = scripts.Where(s => s.IsFavorite).OrderBy(s => s.Name, StringComparer.CurrentCulture).ToList();
        foreach (var s in favorites)
        {
            fav.Node.Children.Add(new FileTreeNodeViewModel(
                FileTreeNodeKind.PyFile, s.Name, s.FilePath)
            {
                MenuRunText = MenuRunText,
                MenuScheduleText = MenuScheduleText,
                MenuNotebookText = MenuNotebookText,
                MenuFavoriteText = MenuRemoveFavoriteText,
                IsRegistered = true,
                IsFavorite = true,
            }.Node);
        }
        if (favorites.Count == 0)
            fav.Node.Children.Add(new FileTreeNodeViewModel(FileTreeNodeKind.Placeholder, NoFavoritesText).Node);
        fav.ChildrenLoaded = true;
        var cats = new FileTreeNodeViewModel(FileTreeNodeKind.Categories, CategoriesText);
        var categories = scripts
            .Select(s => CategoryName(s.Category))
            .GroupBy(c => c, StringComparer.CurrentCulture)
            .OrderBy(g => g.Key, StringComparer.CurrentCulture)
            .ToList();
        cats.BadgeText = Badge(categories.Count);
        foreach (var g in categories)
        {
            var cat = new FileTreeNodeViewModel(FileTreeNodeKind.Category, g.Key)
            {
                BadgeText = Badge(g.Count()),
            };
            cats.Node.Children.Add(cat.Node);
        }
        cats.ChildrenLoaded = true;

        RootNodes.Add(all.Node);
        RootNodes.Add(fav.Node);
        RootNodes.Add(cats.Node);

        // -- 下方真实目录树（懒加载：仅放占位子节点以显示展开箭头） --
        HasScriptPaths = paths.Count > 0;
        foreach (var sp in paths)
        {
            var invalid = !Directory.Exists(sp.Path);
            var root = new FileTreeNodeViewModel(FileTreeNodeKind.ScriptRoot, sp.Path, sp.Path)
            {
                IsInvalid = invalid,
            };
            if (!invalid)
                root.Node.Children.Add(MakePlaceholder());
            RootNodes.Add(root.Node);
        }

        // -- 底部解释器区 --
        Interpreters.Clear();
        try
        {
            foreach (var i in _interpreterService.GetAll())
                Interpreters.Add(i);
        }
        catch (Exception ex)
        {
            DebugWriteError("FileTree: 解释器列表加载失败", ex);
        }
        OnPropertyChanged(nameof(ShowNoInterpreter));

        // 重建完成后按持久化状态恢复展开（CollectionChanged 已同步镜像到 TreeView）
        ApplySavedExpansion();
    }

    /// <summary>手动兜底刷新（Phase E：侧栏刷新按钮/监听变更共用入口）：
    /// 加载态 ProgressRing 可见性由 IsLoading 驱动；让出一帧使 UI 先画出转圈再重建。</summary>
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            await Task.Yield(); // 让加载态先渲染
            Load();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>按持久化状态恢复展开：子级未加载的目录节点走异步枚举（Phase E），
    /// 展开箭头先行置位，子级就绪后自动呈现；仅 Expanding 事件内需延迟，
    /// 此处同步置 IsExpanded 安全。</summary>
    public void ApplySavedExpansion()
    {
        foreach (var node in RootNodes)
        {
            if (node.Content is FileTreeNodeViewModel vm &&
                vm.ExpansionKey.Length > 0 &&
                _expandedKeys.Contains(vm.ExpansionKey))
            {
                if (!vm.ChildrenLoaded && IsDirectoryKind(vm.Kind))
                {
                    vm.ChildrenLoaded = true;
                    _ = CompleteLazyLoadAsync(node, vm, vm.ExpansionKey);
                }
                node.IsExpanded = true;
            }
        }
    }

    /// <summary>TreeView.Expanding：懒加载真实子节点（目录在前、.py 在后，均按名排序）。
    /// 子节点替换延迟到事件返回后执行：事件期间同步改 Children 会令 TreeView
    /// 重入并立即 Collapsed 刚展开的节点（实证：占位符 Clear 触发重入 Expanding+Collapsed）。</summary>
    public void OnNodeExpanding(TreeViewNode node)
    {
        if (node.Content is not FileTreeNodeViewModel vm) return;
#if DEBUG
        DebugLog.WriteLine($"FileTree: Expanding [{vm.ExpansionKey}] kind={vm.Kind} childrenLoaded={vm.ChildrenLoaded}");
#endif
        if (vm.ExpansionKey.Length > 0 && _expandedKeys.Add(vm.ExpansionKey))
            PersistExpansion();

        if (!vm.ChildrenLoaded && IsDirectoryKind(vm.Kind))
        {
            vm.ChildrenLoaded = true; // 前置预占：防重入二次加载
            var key = vm.ExpansionKey;
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dq != null)
                dq.TryEnqueue(() => _ = CompleteLazyLoadAsync(node, vm, key));
            else
                _ = CompleteLazyLoadAsync(node, vm, key);
        }
        else
        {
            // 子级已加载（如 ApplySavedExpansion 同步路径）：直接级联恢复
            CascadeExpandSaved(node);
        }
    }

    /// <summary>懒加载收尾（延迟回调，Phase E 异步化）：后台枚举子级 + 回 UI 线程装配 + 补偿 TreeView 反弹。
    /// 回弹与用户折叠的区分（评审修 1 按节点判定；沿用修 2 重断言口径）：
    /// ① 反弹发生在当事节点自己的装配同步段内——期间 OnNodeCollapsed 对该节点被抑制，
    ///    持久化键不被误移，回调末尾重断言展开态补偿反弹；
    ///    抑制不再覆盖后台枚举 await 窗口（旧计数口径会吞掉期间对任意其他节点的折叠，
    ///    且枚举结束后重断言/CascadeExpandSaved 还会强制重展开并污染持久化集合）；
    /// ② 用户在装配前/后主动折叠——Collapsed 正常移除键，回调检查键仍在集合
    ///    才重断言，故不会强制重展开吞掉用户操作，也不污染持久化集合。
    /// 异步口径：await 续延默认回原同步上下文（UI 线程），装配阶段仍在 UI 线程；
    /// 期间树被 Load 重建时，旧节点脱离 RootNodes，对孤儿节点装配无副作用。</summary>
    private async Task CompleteLazyLoadAsync(TreeViewNode node, FileTreeNodeViewModel vm, string key)
    {
        try
        {
            await LoadDirectoryChildrenAsync(vm);
        }
        catch (Exception ex)
        {
            // 枚举面异常已在内部捕获；此处兜底未预期故障，不让 async 链崩进程
            DebugWriteError($"FileTree: 懒加载异常 [{vm.FullPath}]", ex);
        }

        // 仅当展开意图仍存在（用户未在窗口内折叠）时补偿反弹（评审修 1 保留重断言）
        if (_expandedKeys.Contains(key) && !node.IsExpanded)
            node.IsExpanded = true;
        CascadeExpandSaved(node);
    }

    private static bool IsDirectoryKind(FileTreeNodeKind kind) =>
        kind == FileTreeNodeKind.ScriptRoot || kind == FileTreeNodeKind.Directory;

    /// <summary>级联恢复：已加载子节点若在持久化集合中则继续展开。</summary>
    private void CascadeExpandSaved(TreeViewNode node)
    {
        foreach (var child in node.Children)
        {
            if (child.Content is FileTreeNodeViewModel childVm &&
                childVm.ExpansionKey.Length > 0 &&
                _expandedKeys.Contains(childVm.ExpansionKey))
            {
                // 新装配的深层目录尚未生成 TreeViewItem 容器，单独设置 IsExpanded 不一定
                // 触发 Expanding；这里与根节点恢复路径一致，显式启动懒加载，避免留下空占位行。
                if (!childVm.ChildrenLoaded && IsDirectoryKind(childVm.Kind))
                {
                    childVm.ChildrenLoaded = true;
                    _ = CompleteLazyLoadAsync(child, childVm, childVm.ExpansionKey);
                }
                child.IsExpanded = true;
            }
        }
    }

    /// <summary>TreeView.Collapsed：从展开集合移除并落盘。
    /// 当事节点处于自己的装配同步段内（<see cref="_suppressCollapseFor"/>）时，
    /// 该 Collapsed 是 TreeView 反弹（非用户意图），忽略；其他节点的折叠不受影响（评审修 1）。</summary>
    public void OnNodeCollapsed(TreeViewNode node)
    {
        if (_suppressCollapseFor.Contains(node)) return;

        if (node.Content is FileTreeNodeViewModel vm &&
            vm.ExpansionKey.Length > 0 &&
            _expandedKeys.Remove(vm.ExpansionKey))
        {
#if DEBUG
            DebugLog.WriteLine($"FileTree: Collapsed [{vm.ExpansionKey}]");
#endif
            PersistExpansion();
        }
    }

    /// <summary>TreeView.ItemInvoked（参数为节点 Content）：.py 文件节点 → 已登记选中列表 / 未登记预填新增。</summary>
    public void OnItemInvoked(object? invokedItem)
    {
        if (invokedItem is not FileTreeNodeViewModel vm || vm.Kind != FileTreeNodeKind.PyFile)
            return;

        if (_scriptsByPath.ContainsKey(vm.FullPath))
            RegisteredScriptClicked?.Invoke(vm.FullPath);
        else
            UnregisteredScriptClicked?.Invoke(vm.FullPath);
    }

    /// <summary>树节点右键「运行」（参数为节点 Content）：仅已登记 .py 生效，
    /// 上抛 ScriptRunRequested 由窗口接 RunCoordinator（避免 VM 直依赖运行编排）。</summary>
    public void OnMenuRun(object? nodeContent)
    {
        if (nodeContent is not FileTreeNodeViewModel vm || vm.Kind != FileTreeNodeKind.PyFile)
            return;

        if (_scriptsByPath.TryGetValue(vm.FullPath, out var script))
            ScriptRunRequested?.Invoke(script);
    }

    /// <summary>节点是否展示右键「运行」：已登记的 .py 文件节点。</summary>
    public bool CanRunNode(object? nodeContent) =>
        nodeContent is FileTreeNodeViewModel vm &&
        vm.Kind == FileTreeNodeKind.PyFile &&
        _scriptsByPath.ContainsKey(vm.FullPath);

    public Script? FindRegisteredScript(string filePath) =>
        _scriptsByPath.TryGetValue(filePath, out var script) ? script : null;

    /// <summary>节点是否展示右键「在记事本中打开」（Phase E）：任意 .py 文件节点。</summary>
    public bool CanOpenNotebookNode(object? nodeContent) =>
        nodeContent is FileTreeNodeViewModel vm && vm.Kind == FileTreeNodeKind.PyFile;

    /// <summary>节点是否展示收藏切换（Phase E）：已登记的 .py 文件节点。</summary>
    public bool CanToggleFavoriteNode(object? nodeContent) =>
        nodeContent is FileTreeNodeViewModel vm &&
        vm.Kind == FileTreeNodeKind.PyFile &&
        _scriptsByPath.ContainsKey(vm.FullPath);

    /// <summary>树节点右键「在记事本中打开」（Phase E）：上抛路径，窗口校验存在性后拉起 notepad。</summary>
    public void OnMenuNotebook(object? nodeContent)
    {
        if (nodeContent is FileTreeNodeViewModel vm && vm.Kind == FileTreeNodeKind.PyFile)
            ScriptNotebookRequested?.Invoke(vm.FullPath);
    }

    /// <summary>树节点右键收藏切换（Phase E）：仅已登记 .py 生效，上抛脚本记录。</summary>
    public void OnMenuToggleFavorite(object? nodeContent)
    {
        if (nodeContent is not FileTreeNodeViewModel vm || vm.Kind != FileTreeNodeKind.PyFile) return;
        if (_scriptsByPath.TryGetValue(vm.FullPath, out var script))
            FavoriteToggleRequested?.Invoke(script);
    }

    // ---- 内部实现 ----

    private static string CategoryName(string? category) => category?.Trim() ?? string.Empty;

    private static string Badge(int count) => count.ToString();

    private static TreeViewNode MakePlaceholder() =>
        new() { Content = new FileTreeNodeViewModel(FileTreeNodeKind.Placeholder, string.Empty) };

    /// <summary>目录子级枚举（Phase E 异步化）：Directory.Enumerate* 移后台线程，
    /// 结果回 UI 线程装配，UNC/失效盘不阻塞 UI；异常面保持既有捕获（无权/IO 故障 → 空）。</summary>
    private async Task LoadDirectoryChildrenAsync(FileTreeNodeViewModel vm)
    {
        // 占位移除同样是同步改子级段：按节点抑制包裹（评审修 1，try/finally 保证移除）
        _suppressCollapseFor.Add(vm.Node);
        try
        {
            vm.Node.Children.Clear();
        }
        finally
        {
            _suppressCollapseFor.Remove(vm.Node);
        }

        var path = vm.FullPath;
        if (!Directory.Exists(path))
        {
            vm.IsInvalid = true; // 展示期间目录被删除：退化为失效样式（可观察，绑定即时重求值）
            return;
        }

        List<string> directories = new();
        List<string> files = new();
        try
        {
            // 后台枚举：慢盘/UNC 卡 IO 时不拖 UI 线程
            await Task.Run(() =>
            {
                directories = Directory.EnumerateDirectories(path)
                    .Where(d => !ScriptDirectoryPolicy.ShouldSkipDirectory(d))
                    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                files = Directory.EnumerateFiles(path, "*.py")
                    .Where(f => !IsHidden(f))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });
        }
        catch (UnauthorizedAccessException) { /* 无权目录：保持空 */ }
        catch (IOException) { /* IO 故障：保持空 */ }

        // 枚举期间目录可能已消失：回装配前复查，失效则置灰删除线
        if (!Directory.Exists(path))
        {
            vm.IsInvalid = true;
            return;
        }

        // 装配阶段（UI 线程）：目录在前、.py 在后。
        // 评审修 1：按节点抑制只覆盖本同步段（不含上方后台枚举 await），
        // try/finally 保证异常路径也能移出抑制集合
        _suppressCollapseFor.Add(vm.Node);
        try
        {
            vm.Node.Children.Clear();
            foreach (var dir in directories)
            {
                var child = new FileTreeNodeViewModel(
                    FileTreeNodeKind.Directory, Path.GetFileName(dir), dir);
                child.Node.Children.Add(MakePlaceholder());
                vm.Node.Children.Add(child.Node);
            }

            foreach (var file in files)
            {
                var registered = _scriptsByPath.TryGetValue(file, out var script);
                vm.Node.Children.Add(new FileTreeNodeViewModel(
                    FileTreeNodeKind.PyFile, Path.GetFileName(file), file)
                {
                    MenuRunText = MenuRunText,
                    MenuScheduleText = registered ? MenuScheduleText : string.Empty,
                    MenuNotebookText = MenuNotebookText,
                    MenuFavoriteText = registered
                        ? (script!.IsFavorite ? MenuRemoveFavoriteText : MenuAddFavoriteText)
                        : string.Empty,
                    IsRegistered = registered,
                    IsFavorite = script?.IsFavorite ?? false,
                }.Node);
            }
        }
        finally
        {
            _suppressCollapseFor.Remove(vm.Node);
        }
#if DEBUG
        DebugLog.WriteLine($"FileTree: 懒加载 [{vm.FullPath}] 完成，子节点={vm.Node.Children.Count}");
#endif
    }

    private static bool IsHidden(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
        }
        catch
        {
            return false;
        }
    }

    private void LoadExpansionState()
    {
        var raw = _settingsService.Current.SavedTreeExpanded;
        if (string.IsNullOrWhiteSpace(raw)) return;
        try
        {
            var keys = JsonSerializer.Deserialize<List<string>>(raw);
            if (keys != null)
                foreach (var k in keys)
                    if (!string.IsNullOrWhiteSpace(k))
                        _expandedKeys.Add(k);
        }
        catch (JsonException ex)
        {
            // 脏数据直接弃用，避免启动期崩溃
            DebugWriteError("FileTree: 展开状态反序列化失败，已重置", ex);
        }
    }

    private void PersistExpansion()
    {
        // 恢复阶段的级联展开属于「重放」而非用户操作，合并到最后一次落盘即可，
        // 此处不跳过（集合幂等，重复序列化内容一致），保持单一写路径
        try
        {
            var serialized = JsonSerializer.Serialize(
                _expandedKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            _settingsService.Update(settings => settings.SavedTreeExpanded = serialized);
        }
        catch (Exception ex)
        {
            DebugWriteError("FileTree: 展开状态持久化失败", ex);
        }
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(AllScriptsText));
        OnPropertyChanged(nameof(FavoritesText));
        OnPropertyChanged(nameof(CategoriesText));
        OnPropertyChanged(nameof(NoScriptPathsText));
        OnPropertyChanged(nameof(InterpreterTitle));
        OnPropertyChanged(nameof(NoInterpreterText));
        OnPropertyChanged(nameof(TreeDegradedText));
        OnPropertyChanged(nameof(LoadingText));
        OnPropertyChanged(nameof(OpenSettingsText));
        Load(); // 虚拟节点名称本地化重建（展开集合在 Load 后仍有效）
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugWriteError(string context, Exception ex)
    {
#if DEBUG
        DebugLog.WriteLine($"{context}（{ex.Message}）");
#endif
    }
}
