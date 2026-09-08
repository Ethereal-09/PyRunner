using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using PyRunner.Helpers;
using PyRunner.ViewModels;

namespace PyRunner.Views;

/// <summary>
/// 侧栏视图（Phase C）：两段式文件树（默认）+ 搜索过滤列表（搜索激活时），
/// 底部「已连接解释器」区。无参构造 + <see cref="Initialize"/> 注入 VM
/// （UIA 冒烟与 XAML 设计器兼容）；编辑/删除以事件上抛，
/// 由 MainWindow 承载对话框与删除确认（View 不持有对话框生命周期）。
/// </summary>
public sealed partial class SidebarView : UserControl
{
    // 注意：IsSourceGrouped 时必须显式指定 ItemsPath，否则组内项不被枚举（只渲染组头）
    private readonly CollectionViewSource _groupSource = new()
    {
        IsSourceGrouped = true,
        ItemsPath = new PropertyPath(nameof(ViewModels.ScriptGroup.Items)),
    };
    private ScriptListViewModel? _viewModel;
    private FileTreeViewModel? _treeViewModel;

    /// <summary>「编辑」请求（项右键/更多菜单）。</summary>
    public event EventHandler<ScriptListItemViewModel>? EditRequested;

    /// <summary>「删除」请求（项右键/更多菜单；确认对话框在 MainWindow）。</summary>
    public event EventHandler<ScriptListItemViewModel>? DeleteRequested;

    /// <summary>「运行」请求（列表项右键/更多菜单；窗口接 RunCoordinator）。</summary>
    public event EventHandler<ScriptListItemViewModel>? RunRequested;

    /// <summary>「在记事本中打开」请求（列表项右键/更多菜单，Phase E）。</summary>
    public event EventHandler<ScriptListItemViewModel>? NotebookRequested;

    /// <summary>收藏切换请求（列表项右键/更多菜单，Phase E；树侧经文件树 VM 事件直达窗口）。</summary>
    public event EventHandler<ScriptListItemViewModel>? FavoriteToggleRequested;

    /// <summary>手动兜底刷新请求（Phase E：顶部刷新按钮）。</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>「打开设置」请求（无脚本路径空状态的引导按钮，Phase E）。</summary>
    public event EventHandler? OpenSettingsRequested;

    /// <summary>「打开设置（解释器分组）」请求（无解释器空状态的引导按钮，评审修 5）。</summary>
    public event EventHandler? OpenInterpreterSettingsRequested;

    public SidebarView()
    {
        InitializeComponent();
        // 订阅卫生（评审修复）：全部事件订阅均具名化，Unloaded 统一退订，对齐 DetachLocalization 标准
        Unloaded += OnUnloaded;
    }

    /// <summary>注入 VM 并接线；由 MainWindow 构造后调用一次。</summary>
    public void Initialize(ScriptListViewModel viewModel, FileTreeViewModel treeViewModel)
    {
        _viewModel = viewModel;
        _treeViewModel = treeViewModel;
        DataContext = viewModel;
        _groupSource.Source = viewModel.Groups;
        ScriptListView.ItemsSource = _groupSource.View;

        // 底部解释器区、树空状态文案、降级横幅与加载态走文件树 VM
        InterpreterPanel.DataContext = treeViewModel;
        TreeEmptyOverlay.DataContext = treeViewModel;
        TreeDegradedBanner.DataContext = treeViewModel;
        SidebarLoadingOverlay.DataContext = treeViewModel;

        // UIA 冒烟入口：空状态引导按钮的 AutomationId（沿用既有稳定标识）
        EmptyGuideOverlay.ActionAutomationId = "EmptyDirectorySettingsButton";
        NoResultEmpty.ActionAutomationId = "ClearSearchButton";
        TreeEmptyOverlay.ActionAutomationId = "EmptySettingsButton";
        NoInterpreterEmpty.ActionAutomationId = "NoInterpreterSettingsButton"; // 评审修 5

        // TreeView.RootNodes 为控件自有集合（不可绑定）：订阅 CollectionChanged 同步镜像。
        // VM.Load 全量重建 → 镜像同步 → ApplySavedExpansion 恢复展开
        treeViewModel.RootNodes.CollectionChanged += OnRootNodesChanged;
        SyncTreeRoots();

        FileTree.Expanding += OnTreeExpanding;
        FileTree.Collapsed += OnTreeCollapsed;
        FileTree.SelectionChanged += OnTreeSelectionChanged;
        FileTree.ItemInvoked += OnTreeItemInvoked;

        // 树空状态/空状态引导可见性：双 VM 属性联动（具名订阅，Unloaded 退订）
        viewModel.PropertyChanged += OnListVmPropertyChanged;
        treeViewModel.PropertyChanged += OnTreeVmPropertyChanged;
        UpdateTreeEmptyVisibility();
    }

    private void OnTreeExpanding(TreeView sender, TreeViewExpandingEventArgs e) =>
        _treeViewModel?.OnNodeExpanding(e.Node);

    private void OnTreeCollapsed(TreeView sender, TreeViewCollapsedEventArgs e) =>
        _treeViewModel?.OnNodeCollapsed(e.Node);

    /// <summary>
    /// RootNodes 模式下 SelectionChanged/ItemInvoked 可能返回 TreeViewNode，而文件树 VM
    /// 接受的是节点 Content。统一解包，避免点击 .py 后事件被类型检查静默忽略。
    /// </summary>
    private static object? UnwrapTreeItem(object? item) =>
        item is TreeViewNode node ? node.Content : item;

    /// <summary>
    /// 单击脚本节点的主路径。TreeView 的 ItemInvoked 并不保证在所有鼠标/键盘选择场景触发，
    /// 因此选择变化时立即投影到当前脚本；目录与虚拟节点会由 VM 自动忽略。
    /// </summary>
    private void OnTreeSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs e)
    {
        foreach (var item in e.AddedItems)
        {
            _treeViewModel?.OnItemInvoked(UnwrapTreeItem(item));
            break;
        }
    }

    /// <summary>保留 ItemInvoked 以支持对已选节点按 Enter/再次调用。</summary>
    private void OnTreeItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs e) =>
        _treeViewModel?.OnItemInvoked(UnwrapTreeItem(e.InvokedItem));

    private void OnListVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScriptListViewModel.IsSearchActive) or nameof(ScriptListViewModel.ShowEmptyState))
            UpdateTreeEmptyVisibility();
    }

    private void OnTreeVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileTreeViewModel.HasScriptPaths))
            UpdateTreeEmptyVisibility();
    }

    /// <summary>Unloaded 退订全部具名订阅（评审修复：替代匿名 lambda，防事件持有瞬态 View/VM）。</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Unloaded -= OnUnloaded;
        if (_treeViewModel != null)
        {
            _treeViewModel.RootNodes.CollectionChanged -= OnRootNodesChanged;
            _treeViewModel.PropertyChanged -= OnTreeVmPropertyChanged;
        }
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnListVmPropertyChanged;
        FileTree.Expanding -= OnTreeExpanding;
        FileTree.Collapsed -= OnTreeCollapsed;
        FileTree.SelectionChanged -= OnTreeSelectionChanged;
        FileTree.ItemInvoked -= OnTreeItemInvoked;
    }

    private void OnRootNodesChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncTreeRoots();

    /// <summary>把文件树 VM 的根节点镜像到 TreeView.RootNodes。</summary>
    private void SyncTreeRoots()
    {
        if (_treeViewModel == null) return;
        FileTree.RootNodes.Clear();
        foreach (var node in _treeViewModel.RootNodes)
            FileTree.RootNodes.Add(node);
        if (FileTree.SelectedNode == null && FileTree.RootNodes.Count > 0)
            FileTree.SelectedNode = FileTree.RootNodes[0];
    }

    private void UpdateTreeEmptyVisibility()
    {
        if (_viewModel == null || _treeViewModel == null) return;
        var notSearching = !_viewModel.IsSearchActive;

        // 无脚本目录：引导到设置
        TreeEmptyOverlay.Visibility = !_treeViewModel.HasScriptPaths && notSearching
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 已配置目录但没有可运行脚本：引导回设置管理目录，不再提供手工登记入口。
        EmptyGuideOverlay.Visibility = _treeViewModel.HasScriptPaths && notSearching && _viewModel.ShowEmptyState
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e) =>
        _viewModel?.ClearSearch();

    // ---- 空状态控件（EmptyStateView.ActionRequested = EventHandler 签名） ----

    private void OnEmptyClearSearchRequested(object? sender, EventArgs e) =>
        _viewModel?.ClearSearch();

    private void OnOpenSettingsRequested(object? sender, EventArgs e) =>
        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenInterpreterSettingsRequested(object? sender, EventArgs e) =>
        OpenInterpreterSettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>顶部刷新按钮（Phase E 手动兜底）：窗口据此重建树/列表 + watcher Resync。</summary>
    private void OnRefreshClick(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>侧栏齿轮入口：复用主窗口设置弹窗。</summary>
    private void OnSidebarSettingsClick(object sender, RoutedEventArgs e) =>
        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>快捷键 Ctrl+F 消费：聚焦搜索框。</summary>
    public void FocusSearchBox() => SidebarSearchBox.Focus(FocusState.Programmatic);

    /// <summary>快捷键 Escape 消费：搜索激活时清空搜索（其余情形由 ContentDialog 原生处理）。</summary>
    public bool ClearSearchIfActive()
    {
        if (_viewModel is not { IsSearchActive: true }) return false;
        _viewModel.ClearSearch();
        return true;
    }

    private void OnMenuEditClick(object sender, RoutedEventArgs e)
    {
        if (MenuItemSource(sender) is { } item)
        {
            // 必须先关闭 MenuFlyout 弹层再弹模态对话框：
            // 自动化（UIA InvokePattern）触发的 Click 不经过指针释放，flyout 不会自动收起，
            // 其 Popup 占用会导致 ContentDialog.ShowAsync 永久挂起（真实鼠标点击同样受益于确定性关闭）。
            // 行为假设与间隔防护集中在 DialogHostHelper（MainWindow 侧消费同一套约定）
            DialogHostHelper.CloseContainingFlyoutPopup(sender as DependencyObject);
            EditRequested?.Invoke(this, item);
        }
    }

    private void OnMenuDeleteClick(object sender, RoutedEventArgs e)
    {
        if (MenuItemSource(sender) is { } item)
        {
            DialogHostHelper.CloseContainingFlyoutPopup(sender as DependencyObject);
            DeleteRequested?.Invoke(this, item);
        }
    }

    private void OnMenuRunClick(object sender, RoutedEventArgs e)
    {
        if (MenuItemSource(sender) is { } item)
        {
            // 运行入口与对话框入口同一套 flyout 收起约定（失败路径会弹对话框）
            DialogHostHelper.CloseContainingFlyoutPopup(sender as DependencyObject);
            RunRequested?.Invoke(this, item);
        }
    }

    /// <summary>列表项右键/更多菜单「在记事本中打开」（Phase E）。</summary>
    private void OnMenuNotebookClick(object sender, RoutedEventArgs e)
    {
        if (MenuItemSource(sender) is { } item)
        {
            DialogHostHelper.CloseContainingFlyoutPopup(sender as DependencyObject);
            NotebookRequested?.Invoke(this, item);
        }
    }

    /// <summary>列表项右键/更多菜单收藏切换（Phase E）。</summary>
    private void OnMenuFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (MenuItemSource(sender) is { } item)
        {
            DialogHostHelper.CloseContainingFlyoutPopup(sender as DependencyObject);
            FavoriteToggleRequested?.Invoke(this, item);
        }
    }

    /// <summary>树节点右键菜单 Opening（Phase E）：按节点能力重建条目——
    /// 已登记 .py → 运行/记事本/收藏切换；未登记 .py → 仅记事本；
    /// 其余节点清空 Items（空 flyout 不弹出）。同一 flyout 实例跨次右键复用，
    /// 每次全量重建以反映收藏态最新文案。</summary>
    private void OnTreeMenuOpening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        var content = ((flyout.Target as FrameworkElement)?.DataContext as TreeViewNode)?.Content;
        flyout.Items.Clear();

        if (_treeViewModel?.CanRunNode(content) == true)
            flyout.Items.Add(MakeTreeMenuItem("Content.MenuRunText", OnMenuRunTreeClick));
        if (_treeViewModel?.CanOpenNotebookNode(content) == true)
            flyout.Items.Add(MakeTreeMenuItem("Content.MenuNotebookText", OnMenuNotebookTreeClick));
        if (_treeViewModel?.CanToggleFavoriteNode(content) == true)
            flyout.Items.Add(MakeTreeMenuItem("Content.MenuFavoriteText", OnMenuFavoriteTreeClick));
    }

    /// <summary>树菜单条目工厂：Text 绑定 TreeViewNode.Content 的展示属性。</summary>
    private MenuFlyoutItem MakeTreeMenuItem(string bindingPath, RoutedEventHandler onClick)
    {
        var item = new MenuFlyoutItem();
        item.SetBinding(MenuFlyoutItem.TextProperty, new Binding { Path = new PropertyPath(bindingPath) });
        item.Click += onClick;
        return item;
    }

    /// <summary>树节点右键「运行」：DataContext 为 TreeViewNode，转 VM 后由文件树 VM 过滤上抛。</summary>
    private void OnMenuRunTreeClick(object sender, RoutedEventArgs e)
    {
        var content = ((sender as FrameworkElement)?.DataContext as TreeViewNode)?.Content;
        DialogHostHelper.CloseContainingFlyoutPopup(sender as DependencyObject);
        _treeViewModel?.OnMenuRun(content);
    }

    /// <summary>树节点右键「在记事本中打开」（Phase E）。</summary>
    private void OnMenuNotebookTreeClick(object sender, RoutedEventArgs e)
    {
        var content = ((sender as FrameworkElement)?.DataContext as TreeViewNode)?.Content;
        DialogHostHelper.CloseContainingFlyoutPopup(sender as DependencyObject);
        _treeViewModel?.OnMenuNotebook(content);
    }

    /// <summary>树节点右键收藏切换（Phase E）。</summary>
    private void OnMenuFavoriteTreeClick(object sender, RoutedEventArgs e)
    {
        var content = ((sender as FrameworkElement)?.DataContext as TreeViewNode)?.Content;
        DialogHostHelper.CloseContainingFlyoutPopup(sender as DependencyObject);
        _treeViewModel?.OnMenuToggleFavorite(content);
    }

    /// <summary>MenuFlyoutItem 的 DataContext 继承自列表项容器，取回项 VM。</summary>
    private static ScriptListItemViewModel? MenuItemSource(object sender) =>
        (sender as FrameworkElement)?.DataContext as ScriptListItemViewModel;
}
