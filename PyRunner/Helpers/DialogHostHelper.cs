using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PyRunner.Helpers;

/// <summary>
/// Flyout → ContentDialog 过渡辅助。集中承载两个 WinUI 3 行为假设（Phase B 评审定稿；
/// Phase D 迁移对话框入口时只需重验本类一处）：
/// <list type="number">
/// <item>程序化触发的 MenuFlyoutItem Click（如 UIA InvokePattern）不经过指针释放，
/// flyout 不会自动收起；其 Popup 占用会使后续 <c>ContentDialog.ShowAsync</c> 永久挂起。
/// 对策：沿可视树向上找到承载 flyout 的 Popup 并同步关闭（<see cref="CloseContainingFlyoutPopup"/>）。</item>
/// <item>同帧内「Popup 关闭动画」与「新建 ContentDialog」冲突时，ShowAsync 会卡挂起
/// 或抛 0x80000019。对策：让出一个动画周期（<see cref="FlyoutDismissGuard"/>）再弹窗。</item>
/// </list>
/// </summary>
public static class DialogHostHelper
{
    /// <summary>MenuFlyout 收起动画与模态弹窗打开的间隔防护（一个动画周期）。</summary>
    public static readonly TimeSpan FlyoutDismissGuard = TimeSpan.FromMilliseconds(200);

    /// <summary>把对话框挂到指定 XamlRoot，并显式继承宿主元素主题。
    /// ContentDialog 位于独立 Popup 视觉树，不会自动继承主窗口根布局的 RequestedTheme。</summary>
    public static void Prepare(ContentDialog dialog, XamlRoot xamlRoot, ElementTheme? requestedTheme = null)
    {
        dialog.XamlRoot = xamlRoot;
        dialog.RequestedTheme = requestedTheme
            ?? (xamlRoot.Content as FrameworkElement)?.RequestedTheme
            ?? ElementTheme.Default;
    }

    /// <summary>沿可视树向上找到承载 flyout 的 Popup 并关闭（等价 flyout.Hide 的确定性路径）。
    /// 非 flyout 入口（source 不在 Popup 子树内）调用是安全的空操作。</summary>
    public static void CloseContainingFlyoutPopup(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is Microsoft.UI.Xaml.Controls.Primitives.Popup popup)
            {
                popup.IsOpen = false;
                return;
            }
            current = VisualTreeHelper.GetParent(current);
        }
    }

    /// <summary>菜单（flyout）入口打开模态对话框前的标准等待序列：
    /// 先确定性关闭 flyout Popup，再让出一个动画周期。
    /// 非 flyout 触发的入口可直接 <c>await Task.Delay(FlyoutDismissGuard)</c> 或跳过本方法。</summary>
    public static async Task ShowAfterFlyoutDismissAsync(DependencyObject? menuSource)
    {
        CloseContainingFlyoutPopup(menuSource);
        await Task.Delay(FlyoutDismissGuard);
    }
}
