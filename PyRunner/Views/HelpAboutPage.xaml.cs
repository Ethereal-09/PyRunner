using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PyRunner.Models;
using PyRunner.ViewModels;

namespace PyRunner.Views;

/// <summary>
/// 帮助与关于页。页面仅负责视图绑定和窄窗口排版，状态、命令与系统能力均由 ViewModel/服务承载。
/// </summary>
public sealed partial class HelpAboutPage : UserControl, IDisposable
{
    private bool _disposed;

    public HelpAboutViewModel ViewModel { get; }

    public event EventHandler<HelpSettingsSection>? SettingsRequested;

    public HelpAboutPage(HelpAboutViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.SettingsNavigationRequested += OnSettingsNavigationRequested;
        ViewModel.Refresh();
    }

    /// <summary>重新进入页面时刷新版本、运行时与默认解释器信息。</summary>
    public void Refresh() => ViewModel.Refresh();

    private void OnSettingsNavigationRequested(object? sender, HelpSettingsSection section) =>
        SettingsRequested?.Invoke(this, section);

    private void OnHelpScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // ScrollViewer 会以无限横向空间测量内容；按实际视口约束宽度，避免窄窗口横向裁切。
        var contentWidth = Math.Min(1050, Math.Max(0, e.NewSize.Width - 56));
        ResponsiveRoot.Width = contentWidth;
        ApplyResponsiveLayout(contentWidth < 720);
    }

    private void ApplyResponsiveLayout(bool narrow)
    {
        HeroFirstColumn.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(64);
        HeroSecondColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(HeroDetails, narrow ? 0 : 1);
        Grid.SetRow(HeroDetails, narrow ? 1 : 0);
        HeroDetails.Margin = narrow ? new Thickness(0, 14, 0, 0) : new Thickness(0);

        DetailsSecondColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(SoftwareCard, narrow ? 0 : 1);
        Grid.SetRow(SoftwareCard, narrow ? 1 : 0);
        SoftwareCard.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(6, 0, 0, 0);

        QuickSecondColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(QuickCard2, narrow ? 0 : 1);
        Grid.SetRow(QuickCard2, narrow ? 1 : 0);
        QuickCard2.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(6, 0, 0, 5);
        Grid.SetRow(QuickCard3, narrow ? 2 : 1);
        QuickCard3.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(0, 5, 6, 0);
        Grid.SetColumn(QuickCard4, narrow ? 0 : 1);
        Grid.SetRow(QuickCard4, narrow ? 3 : 1);
        QuickCard4.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(6, 5, 0, 0);

        FooterActions.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
        FooterActions.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ViewModel.SettingsNavigationRequested -= OnSettingsNavigationRequested;
        ViewModel.Dispose();
    }
}
