using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PyRunner.Models;
using PyRunner.ViewModels;

namespace PyRunner.Views;

/// <summary>
/// 脚本编辑对话框。
/// 点击主按钮（保存）时在 Closing 中执行 VM 校验：失败则取消关闭、字段级红字展示。
/// 文案全部来自本地化资源（经由 <see cref="ScriptEditViewModel"/> 的展示属性）。
/// </summary>
public sealed partial class ScriptEditDialog : ContentDialog
{
    private Window? _ownerWindow;

    public ScriptEditViewModel ViewModel { get; }

    public ScriptEditDialog(ScriptEditViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;

        // 对话框关闭时退订 VM 的语言切换订阅（服务单例不得持有瞬态 VM）
        Closed += (_, _) => ViewModel.DetachLocalization();
    }

    /// <summary>打开前加载待编辑记录。</summary>
    public void Prepare(Script existing) => ViewModel.Prepare(existing);

    /// <summary>保存成功后的文件路径（调用方用于保持列表选中）。</summary>
    public string SavedFilePath => ViewModel.FilePath.Trim();

    /// <summary>宿主窗口（「浏览…」的 FileOpenPicker 需要窗口句柄初始化）。</summary>
    public void SetOwnerWindow(Window window) => _ownerWindow = window;

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        // 仅拦截「保存」；取消/关闭直接放行
        if (args.Result == ContentDialogResult.Primary && !ViewModel.Save())
            args.Cancel = true;
    }

    /// <summary>「浏览…」：FileOpenPicker 过滤 *.py，选中后回填 FilePath（PRD §5.6）。</summary>
    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            if (_ownerWindow != null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_ownerWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }

            picker.FileTypeFilter.Add(".py");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;

            var file = await picker.PickSingleFileAsync();
            if (file != null)
                ViewModel.FilePath = file.Path;
        }
        catch (Exception ex)
        {
            // 选择器失败不得阻断手工输入路径：仅记日志
            System.Diagnostics.Debug.WriteLine($"ScriptEditDialog: 文件选择器失败（{ex.Message}）");
        }
    }
}
