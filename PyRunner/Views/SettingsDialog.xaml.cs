using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PyRunner.ViewModels;

namespace PyRunner.Views;

/// <summary>
/// 设置弹窗（Phase C 交付 9）：Acrylic 四分组——脚本路径 / 解释器 / 运行选项 / 语言。
/// 变更即时持久化（DB 同步写、设置经 JsonSettingsService 防抖 Save），
/// OK/Cancel 仅负责关闭（不存在「未保存草稿」概念）。
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private Window? _ownerWindow;

    public SettingsViewModel ViewModel { get; }

    /// <summary>打开时直达解释器分组（「无解释器」空状态引导，评审修 5）：
    /// 滚动到分组二标题 + 焦点落「添加解释器」按钮（键盘可达）。</summary>
    public bool FocusInterpretersOnOpen { get; set; }

    /// <summary>从帮助页的“添加脚本目录”入口打开时，直达脚本路径分组。</summary>
    public bool FocusScriptPathsOnOpen { get; set; }

    public SettingsDialog(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        // 对话框关闭时退订 VM 的语言切换订阅（服务单例不得持有瞬态 VM）
        Closed += (_, _) => ViewModel.DetachLocalization();
        Opened += OnDialogOpened;
    }

    private async void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        Opened -= OnDialogOpened;
        if (!FocusInterpretersOnOpen && !FocusScriptPathsOnOpen) return;

        // 延迟一拍：ContentDialog 在 Opened 后仍会把焦点交给默认按钮，
        // 等默认焦点落定后再滚动+聚焦解释器分组（评审修 5）
        await Task.Delay(300);

        try
        {
            var targetTitle = FocusInterpretersOnOpen ? InterpretersGroupTitle : ScriptPathsGroupTitle;
            var targetButton = FocusInterpretersOnOpen ? AddInterpreterButton : AddFolderButton;
            // 滚动定位：分组标题在 ScrollViewer 内容坐标系内的 Y + 当前偏移
            var transform = targetTitle.TransformToVisual(SettingsScroller);
            var position = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            SettingsScroller.ChangeView(null, position.Y + SettingsScroller.VerticalOffset, null, disableAnimation: true);
            targetButton.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            // 定位失败不影响弹窗本体：仅留痕
            DebugWrite("解释器分组定位失败", ex);
        }
    }

    /// <summary>宿主窗口（文件夹/文件选择器需要窗口句柄初始化）。</summary>
    public void SetOwnerWindow(Window window) => _ownerWindow = window;

    /// <summary>分组一：「添加文件夹…」→ FolderPicker 回填后即时入库。</summary>
    private async void OnAddFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            InitPickerWithOwner(picker);
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
                await ViewModel.AddScriptPathAsync(folder.Path);
        }
        catch (Exception ex)
        {
            // 选择器失败不得阻断手工流程：仅记日志
            DebugWrite("文件夹选择器失败", ex);
        }
    }

    /// <summary>分组二：「添加解释器…」→ FileOpenPicker 选 python.exe；
    /// 版本检测在 VM 内 Task.Run 异步执行（行内 ProgressRing）。</summary>
    private async void OnAddInterpreterClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            InitPickerWithOwner(picker);
            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;

            var file = await picker.PickSingleFileAsync();
            if (file != null)
                await ViewModel.AddInterpreterAsync(file.Path);
        }
        catch (Exception ex)
        {
            DebugWrite("解释器选择器失败", ex);
        }
    }

    private void OnRemovePathClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ScriptPathRow row)
            ViewModel.RemoveScriptPath(row);
    }

    private void OnRemoveInterpreterClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InterpreterRow row)
            ViewModel.RemoveInterpreter(row);
    }

    /// <summary>分组四：语言下拉变更即时生效（LocalizationService 内部写回 settings）。</summary>
    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.ApplyLanguageSelection();

    private void InitPickerWithOwner(object picker)
    {
        if (_ownerWindow == null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_ownerWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugWrite(string context, Exception ex)
    {
#if DEBUG
        DebugLog.WriteLine($"SettingsDialog: {context}（{ex.Message}）");
#endif
    }
}
