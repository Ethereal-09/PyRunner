using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PyRunner.Helpers;
using PyRunner.Models;

namespace PyRunner.Services;

public sealed class DependencyInteractionService : IDependencyInteractionService
{
    private readonly ILocalizationService _localization;
    public DependencyInteractionService(ILocalizationService localization) => _localization = localization;

    public Task<bool> ConfirmInspectionAsync(
        IReadOnlyList<DependencyRisk> risks,
        CancellationToken cancellationToken = default) =>
        ShowAsync(
            _localization["Dependency_InspectConfirmTitle"],
            BuildRiskContent(_localization["Dependency_InspectConfirmBody"], risks),
            _localization["Dependency_Check"],
            cancellationToken);

    public Task<bool> ConfirmInstallAsync(
        string interpreterName,
        string interpreterPath,
        string requirementsPath,
        int changeCount,
        IReadOnlyList<DependencyRisk> risks,
        CancellationToken cancellationToken = default)
    {
        var header = string.Format(
            _localization["Dependency_InstallConfirmBody"],
            interpreterName,
            interpreterPath,
            requirementsPath,
            changeCount);
        return ShowAsync(
            _localization["Dependency_InstallConfirmTitle"],
            BuildRiskContent(header, risks),
            _localization["Dependency_Install"],
            cancellationToken);
    }

    private FrameworkElement BuildRiskContent(string header, IReadOnlyList<DependencyRisk> risks)
    {
        var panel = new StackPanel { Spacing = 10, MaxWidth = 680 };
        panel.Children.Add(new TextBlock
        {
            Text = header,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        panel.Children.Add(new TextBlock
        {
            Text = _localization["Dependency_InstallCodeRisk"],
            TextWrapping = TextWrapping.Wrap,
            Foreground = Views.ThemeBrushes.Get("StatusRunningBrush", 0xFFFFD479),
        });
        foreach (var risk in risks)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"• {_localization[risk.MessageKey]}: {risk.DisplaySource}",
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
        }
        return new ScrollViewer
        {
            Content = panel,
            MaxHeight = 480,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private async Task<bool> ShowAsync(
        string title,
        FrameworkElement content,
        string primaryText,
        CancellationToken cancellationToken)
    {
        try
        {
            if (Application.Current is not App app || app.CurrentXamlRoot is not { } xamlRoot)
                return false;
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryText,
                CloseButtonText = _localization["Button_Cancel"],
                DefaultButton = ContentDialogButton.Close,
            };
            DialogHostHelper.Prepare(dialog, xamlRoot);
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            using var registration = cancellationToken.Register(() => dispatcher.TryEnqueue(() =>
            {
                try { dialog.Hide(); }
                catch { }
            }));
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch
        {
            return false;
        }
    }
}
