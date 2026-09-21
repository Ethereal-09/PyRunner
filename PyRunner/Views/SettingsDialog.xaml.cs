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
        Closing += OnDialogClosing;
        Opened += OnDialogOpened;
        ViewModel.TerminalFontSizePreviewChanged += OnTerminalFontSizePreviewChanged;
    }

    private async void OnDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        Opened -= OnDialogOpened;
        await InitializeTerminalPreviewAsync();
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

    private void OnDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result == ContentDialogResult.Primary) ViewModel.CommitTerminalAppearance();
        else ViewModel.CancelTerminalAppearance();
        ViewModel.TerminalFontSizePreviewChanged -= OnTerminalFontSizePreviewChanged;
        try { TerminalPreviewWebView.Close(); } catch { }
    }

    private async Task InitializeTerminalPreviewAsync()
    {
        try
        {
            await TerminalPreviewWebView.EnsureCoreWebView2Async();
            var core = TerminalPreviewWebView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.SetVirtualHostNameToFolderMapping("pyrunner-terminal-preview.local",
                Path.Combine(AppContext.BaseDirectory, "Terminal", "wwwroot"),
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    uri.Port != 443 || !uri.Host.Equals("pyrunner-terminal-preview.local", StringComparison.OrdinalIgnoreCase) ||
                    !uri.AbsolutePath.Equals("/preview.html", StringComparison.Ordinal))
                    args.Cancel = true;
            };
            core.Navigate($"https://pyrunner-terminal-preview.local/preview.html?theme={(ActualTheme == ElementTheme.Light ? "light" : "dark")}&fontSize={(int)ViewModel.TerminalFontSizeDraft}");
        }
        catch
        {
            TerminalPreviewWebView.Visibility = Visibility.Collapsed;
        }
    }

    private void OnTerminalFontSizePreviewChanged(int fontSize)
    {
        try
        {
            TerminalPreviewWebView.CoreWebView2?.PostWebMessageAsJson(
                System.Text.Json.JsonSerializer.Serialize(new { type = "fontSize", fontSize }));
        }
        catch { }
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

    private void OnAiKeyPasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.AiKeyDraft = AiKeyBox.Password;

    private void OnSaveAiClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SaveAiConfiguration();
        AiKeyBox.Password = string.Empty;
    }

    private async void OnTestAiClick(object sender, RoutedEventArgs e)
    {
        await ViewModel.TestAiConnectionAsync();
        AiKeyBox.Password = string.Empty;
    }

    private void OnDeleteAiKeyClick(object sender, RoutedEventArgs e)
    {
        ViewModel.DeleteAiCredential();
        AiKeyBox.Password = string.Empty;
    }

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
