using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PyRunner.Models;
using PyRunner.Services;
using PyRunner.ViewModels;

namespace PyRunner.Views;

public sealed partial class ScheduledTasksPage : Page, IDisposable
{
    private readonly IScheduledTaskService _tasks;
    private readonly DataTemplate _wideTaskTemplate;
    public ScheduledTasksViewModel ViewModel { get; }
    public event Action<Script?>? AddRequested;
    public event Action<ScheduledTask>? EditRequested;
    public event Action<ScheduledTask>? RunNowRequested;
    public event Action<ScheduledTask>? DeleteRequested;
    public event Action<ScheduledTask>? HistoryRequested;
    public event Action? Changed;

    public ScheduledTasksPage(ScheduledTasksViewModel viewModel, IScheduledTaskService tasks)
    {
        ViewModel = viewModel; _tasks = tasks; InitializeComponent();
        _wideTaskTemplate = ScheduleList.ItemTemplate;
        DataContext = viewModel; viewModel.Load();
    }
    public void Refresh() => ViewModel.Load();
    public void ShowOperationError() => OperationError.IsOpen = true;
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ScheduleList is null || HeaderRow is null) return;
        var compact = e.NewSize.Width < 860;
        HeaderRow.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (PageRoot.Content is Grid grid)
            grid.RowDefinitions[1].Height = compact ? new GridLength(0) : new GridLength(42);
        ScheduleList.ItemTemplate = compact ? (DataTemplate)Resources["CompactTaskTemplate"] : _wideTaskTemplate;
    }
    public void RequestAdd(Script script) => AddRequested?.Invoke(script);
    private void OnAddClick(object sender, RoutedEventArgs e) => AddRequested?.Invoke(null);
    private void OnEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not ScheduledTaskItemViewModel item || item.Task.Enabled == toggle.IsOn) return;
        try
        {
            item.Task.Enabled = toggle.IsOn; _tasks.SetEnabled(item.Task.Id, toggle.IsOn); Changed?.Invoke();
        }
        catch (Exception ex)
        {
            OperationError.IsOpen = true;
            System.Diagnostics.Debug.WriteLine($"ScheduledTasksPage: toggle failed ({ex.Message})");
        }
        finally { ViewModel.Load(); }
    }
    private static ScheduledTask? Source(object sender) => ((sender as FrameworkElement)?.DataContext as ScheduledTaskItemViewModel)?.Task;
    private void OnRunNowClick(object sender, RoutedEventArgs e) { if (Source(sender) is { } task) RunNowRequested?.Invoke(task); }
    private void OnEditClick(object sender, RoutedEventArgs e) { if (Source(sender) is { } task) EditRequested?.Invoke(task); }
    private void OnDeleteClick(object sender, RoutedEventArgs e) { if (Source(sender) is { } task) DeleteRequested?.Invoke(task); }
    private void OnRetryClick(object sender, RoutedEventArgs e) => Refresh();
    private void OnHistoryClick(object sender, RoutedEventArgs e) { if (Source(sender) is { } task) HistoryRequested?.Invoke(task); }
    public void Dispose() => ViewModel.DetachLocalization();
}
