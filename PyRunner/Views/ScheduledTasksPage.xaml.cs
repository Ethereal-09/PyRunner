using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PyRunner.Models;
using PyRunner.Services;
using PyRunner.ViewModels;

namespace PyRunner.Views;

public sealed partial class ScheduledTasksPage : Page, IDisposable
{
    private readonly IScheduledTaskService _tasks;
    public ScheduledTasksViewModel ViewModel { get; }
    public event Action<Script?>? AddRequested;
    public event Action<ScheduledTask>? EditRequested;
    public event Action<ScheduledTask>? RunNowRequested;
    public event Action<ScheduledTask>? DeleteRequested;
    public event Action? Changed;

    public ScheduledTasksPage(ScheduledTasksViewModel viewModel, IScheduledTaskService tasks)
    {
        ViewModel = viewModel; _tasks = tasks; InitializeComponent(); DataContext = viewModel; viewModel.Load();
    }
    public void Refresh() => ViewModel.Load();
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
            System.Diagnostics.Debug.WriteLine($"ScheduledTasksPage: toggle failed ({ex.Message})");
        }
        finally { ViewModel.Load(); }
    }
    private static ScheduledTask? Source(object sender) => ((sender as FrameworkElement)?.DataContext as ScheduledTaskItemViewModel)?.Task;
    private void OnRunNowClick(object sender, RoutedEventArgs e) { if (Source(sender) is { } task) RunNowRequested?.Invoke(task); }
    private void OnEditClick(object sender, RoutedEventArgs e) { if (Source(sender) is { } task) EditRequested?.Invoke(task); }
    private void OnDeleteClick(object sender, RoutedEventArgs e) { if (Source(sender) is { } task) DeleteRequested?.Invoke(task); }
    public void Dispose() => ViewModel.DetachLocalization();
}
