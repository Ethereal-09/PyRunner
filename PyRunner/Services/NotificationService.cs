using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PyRunner.Helpers;

namespace PyRunner.Services;

/// <summary>通知服务的用户选择结果（对应 ContentDialog 三按钮）。</summary>
public enum NotifyChoice
{
    /// <summary>无操作（关闭按钮/遮罩/Esc）。</summary>
    None,

    /// <summary>主按钮（Primary）。</summary>
    Primary,

    /// <summary>次按钮（Secondary）。</summary>
    Secondary,
}

/// <summary>
/// 友好通知服务契约（Phase E 交付 16）：错误提示统一走本服务，
/// 文案以用户语言表述、给出修正方案、不暴露技术细节（PRD §6.8.2 原则）。
/// 全部入口返回用户选择，导航/修正动作由调用方执行（服务不持有窗口逻辑）。
/// </summary>
public interface INotificationService
{
    /// <summary>A-24：无可用解释器 → 友好提示 + [打开设置]。返回是否打开设置。</summary>
    Task<bool> NotifyNoInterpreterAsync(XamlRoot xamlRoot);

    /// <summary>A-25：脚本文件已被移动或删除 → [重新选择文件] [移除记录]。
    /// Primary=重新选择、Secondary=移除记录、None=暂不处理。</summary>
    Task<NotifyChoice> NotifyScriptMissingAsync(XamlRoot xamlRoot);

    /// <summary>文件不存在（如「在记事本中打开」目标已失效）。</summary>
    Task NotifyFileMissingAsync(XamlRoot xamlRoot);

    /// <summary>WebView2 运行时缺失 → 友好提示 + [下载安装]（打开官方下载页）。</summary>
    Task NotifyWebView2MissingAsync(XamlRoot xamlRoot);

    /// <summary>通用运行错误提示（文案键已本地化，如 Run_AlreadyRunning）。</summary>
    Task NotifyRunErrorAsync(XamlRoot xamlRoot, string errorKey);

    /// <summary>脚本非零退出时的可选失败提醒。</summary>
    Task NotifyRunFailedAsync(XamlRoot xamlRoot, string scriptName);

    /// <summary>通用友好错误提示（标题键 + 内容键）。</summary>
    Task NotifyErrorAsync(XamlRoot xamlRoot, string titleKey, string contentKey);
}

/// <summary>通知服务实现：ContentDialog 统一承载，文案全部资源化双语。</summary>
public sealed class NotificationService : INotificationService
{
    private readonly ILocalizationService _localization;

    public NotificationService(ILocalizationService localization)
    {
        _localization = localization;
    }

    public async Task<bool> NotifyNoInterpreterAsync(XamlRoot xamlRoot)
    {
        var dialog = BuildDialog(xamlRoot,
            _localization["Run_NoInterpreter_Title"],
            _localization["Run_NoInterpreter"],
            primaryText: _localization["Button_OpenSettings"]);

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public async Task<NotifyChoice> NotifyScriptMissingAsync(XamlRoot xamlRoot)
    {
        var dialog = BuildDialog(xamlRoot,
            _localization["Run_Error_Title"],
            _localization["Notify_ScriptMissing"],
            primaryText: _localization["Notify_ReselectFile"],
            secondaryText: _localization["Notify_RemoveRecord"]);

        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => NotifyChoice.Primary,
            ContentDialogResult.Secondary => NotifyChoice.Secondary,
            _ => NotifyChoice.None,
        };
    }

    public Task NotifyFileMissingAsync(XamlRoot xamlRoot) =>
        ShowInfoAsync(xamlRoot, _localization["Notify_Title"], _localization["Notify_FileMissing"]);

    public async Task NotifyWebView2MissingAsync(XamlRoot xamlRoot)
    {
        var dialog = BuildDialog(xamlRoot,
            _localization["Notify_Title"],
            _localization["Notify_WebView2Missing"],
            primaryText: _localization["Notify_Download"]);

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(
                    new Uri("https://developer.microsoft.com/microsoft-edge/webview2/"));
            }
            catch
            {
                // 打开链接失败不影响主流程
            }
        }
    }

    public Task NotifyRunErrorAsync(XamlRoot xamlRoot, string errorKey) =>
        ShowInfoAsync(xamlRoot, _localization["Run_Error_Title"], _localization[errorKey]);

    public Task NotifyRunFailedAsync(XamlRoot xamlRoot, string scriptName) =>
        ShowInfoAsync(xamlRoot, _localization["Run_Error_Title"],
            string.Format(_localization["Notify_RunFailed"], scriptName));

    public Task NotifyErrorAsync(XamlRoot xamlRoot, string titleKey, string contentKey) =>
        ShowInfoAsync(xamlRoot, _localization[titleKey], _localization[contentKey]);

    // ---- 内部实现 ----

    private async Task ShowInfoAsync(XamlRoot xamlRoot, string title, string content)
    {
        var dialog = BuildDialog(xamlRoot, title, content, primaryText: null);
        await dialog.ShowAsync();
    }

    /// <summary>统一构建：primaryText 为 null 时仅展示「确定」关闭按钮。</summary>
    private ContentDialog BuildDialog(XamlRoot xamlRoot, string title, string content,
        string? primaryText, string? secondaryText = null)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            DefaultButton = ContentDialogButton.Primary,
        };
        DialogHostHelper.Prepare(dialog, xamlRoot);

        if (primaryText != null)
        {
            dialog.PrimaryButtonText = primaryText;
            if (secondaryText != null)
                dialog.SecondaryButtonText = secondaryText;
            dialog.CloseButtonText = _localization["Button_Cancel"];
        }
        else
        {
            dialog.CloseButtonText = _localization["Button_OK"];
            dialog.DefaultButton = ContentDialogButton.Close;
        }

        return dialog;
    }
}
