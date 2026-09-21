using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PyRunner.Helpers;
using PyRunner.Models;

namespace PyRunner.Services.AI;

public sealed class AiInteractionService : IAiInteractionService
{
    private readonly ILocalizationService _localization;
    public AiInteractionService(ILocalizationService localization) => _localization = localization;

    public Task<bool> ConfirmSendAsync(AiSendPreview preview, string maskedPreview, CancellationToken cancellationToken)
    {
        var details = string.Format(_localization["AI_SendPreviewBody"], preview.Host, preview.Model,
            preview.Scope, preview.CharacterCount, preview.EstimatedTokens, preview.Findings.Count);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap });
        if (preview.Findings.Count > 0)
            panel.Children.Add(new TextBlock { Text = _localization["AI_SecretWarning"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DangerBrush"],
                TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBox { Text = maskedPreview, IsReadOnly = true, AcceptsReturn = true,
            MaxHeight = 180, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
        return ShowAsync("AI_SendPreviewTitle", panel, "AI_Send", cancellationToken);
    }

    public Task<bool> ConfirmApplyAsync(AiCodeCandidate candidate, string localDiff, CancellationToken cancellationToken)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = _localization["AI_OutputWarning"], TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = candidate.Summary, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBox { Text = localDiff, IsReadOnly = true, AcceptsReturn = true,
            MaxHeight = 260, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            TextWrapping = TextWrapping.NoWrap });
        return ShowAsync("AI_ApplyTitle", panel, "AI_Apply", cancellationToken);
    }

    public async Task ShowDiffAsync(string localDiff, CancellationToken cancellationToken)
    {
        if (App.Current is not App app || app.CurrentXamlRoot is not { } root) return;
        var content = new TextBox
        {
            Text = localDiff, IsReadOnly = true, AcceptsReturn = true, MaxHeight = 520,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.NoWrap,
        };
        var dialog = new ContentDialog
        {
            Title = _localization["AI_ViewDiff"], Content = content,
            CloseButtonText = _localization["Button_Close"], DefaultButton = ContentDialogButton.Close,
        };
        DialogHostHelper.Prepare(dialog, root);
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        using var registration = cancellationToken.Register(() => dispatcher.TryEnqueue(() => { try { dialog.Hide(); } catch { } }));
        try { await dialog.ShowAsync(); } catch { }
    }

    public Task<bool> ConfirmDeleteConversationAsync(string title, CancellationToken cancellationToken)
    {
        var body = new TextBlock
        {
            Text = string.Format(_localization["AI_DeleteConversationBody"], title),
            TextWrapping = TextWrapping.Wrap,
        };
        return ShowAsync("AI_DeleteConversationTitle", body, "AI_DeleteConversation", cancellationToken);
    }

    private async Task<bool> ShowAsync(string titleKey, object content, string primaryKey, CancellationToken cancellationToken)
    {
        if (App.Current is not App app || app.CurrentXamlRoot is not { } root) return false;
        var dialog = new ContentDialog
        {
            Title = _localization[titleKey], Content = content,
            PrimaryButtonText = _localization[primaryKey], CloseButtonText = _localization["Button_Cancel"],
            DefaultButton = ContentDialogButton.Close,
        };
        DialogHostHelper.Prepare(dialog, root);
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        using var registration = cancellationToken.Register(() => dispatcher.TryEnqueue(() => { try { dialog.Hide(); } catch { } }));
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        catch { return false; }
    }
}
