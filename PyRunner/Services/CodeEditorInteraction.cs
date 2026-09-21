using Microsoft.UI.Xaml.Controls;
using PyRunner.Editor;
using PyRunner.Helpers;

namespace PyRunner.Services;

public sealed class CodeEditorInteraction : ICodeEditorInteraction
{
    private readonly ILocalizationService _localization;
    public CodeEditorInteraction(ILocalizationService localization) => _localization = localization;

    public async Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(CancellationToken cancellationToken = default)
    {
        var result = await ShowAsync("Editor_UnsavedTitle", "Editor_UnsavedBody",
            "Editor_Save", "Editor_Discard", cancellationToken);
        return result switch
        {
            ContentDialogResult.Primary => UnsavedChangesChoice.Save,
            ContentDialogResult.Secondary => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel,
        };
    }

    public async Task<ExternalConflictChoice> ResolveExternalConflictAsync(CancellationToken cancellationToken = default)
    {
        if (App.Current is not App app || app.CurrentXamlRoot is not { } root) return ExternalConflictChoice.Cancel;
        var choices = new ComboBox
        {
            ItemsSource = new[]
            {
                _localization["Editor_ConflictReload"],
                _localization["Editor_ConflictSaveAs"],
                _localization["Editor_ConflictOverwrite"],
            },
            SelectedIndex = 0,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = _localization["Editor_ConflictBody"], TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap });
        panel.Children.Add(choices);
        var dialog = new ContentDialog
        {
            Title = _localization["Editor_ConflictTitle"], Content = panel,
            PrimaryButtonText = _localization["Button_Continue"],
            CloseButtonText = _localization["Button_Cancel"], DefaultButton = ContentDialogButton.Close,
        };
        DialogHostHelper.Prepare(dialog, root);
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return ExternalConflictChoice.Cancel;
        return choices.SelectedIndex switch
        {
            0 => ExternalConflictChoice.Reload,
            1 => ExternalConflictChoice.SaveAs,
            2 => ExternalConflictChoice.Overwrite,
            _ => ExternalConflictChoice.Cancel,
        };
    }

    public async Task<bool> ConfirmRestoreDraftAsync(DateTimeOffset savedAtUtc, CancellationToken cancellationToken = default) =>
        await ShowAsync("Editor_DraftTitle", "Editor_DraftBody", "Editor_DraftRestore", null, cancellationToken) ==
        ContentDialogResult.Primary;

    public async Task<string?> PickSaveAsPathAsync(string currentPath, CancellationToken cancellationToken = default)
    {
        if (App.Current is not App app || app.CurrentWindow is not { } window) return null;
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(currentPath) + "-copy",
        };
        picker.FileTypeChoices.Add("Python", new List<string> { ".py" });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private async Task<ContentDialogResult> ShowAsync(
        string titleKey, string bodyKey, string primaryKey, string? secondaryKey, CancellationToken cancellationToken)
    {
        if (App.Current is not App app || app.CurrentXamlRoot is not { } root) return ContentDialogResult.None;
        var dialog = new ContentDialog
        {
            Title = _localization[titleKey], Content = _localization[bodyKey],
            PrimaryButtonText = _localization[primaryKey],
            SecondaryButtonText = secondaryKey is null ? string.Empty : _localization[secondaryKey],
            CloseButtonText = _localization["Button_Cancel"], DefaultButton = ContentDialogButton.Close,
        };
        DialogHostHelper.Prepare(dialog, root);
        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        using var registration = cancellationToken.Register(() => dispatcher.TryEnqueue(() => { try { dialog.Hide(); } catch { } }));
        try { return await dialog.ShowAsync(); } catch { return ContentDialogResult.None; }
    }
}
