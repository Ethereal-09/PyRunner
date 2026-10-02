using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using PyRunner.Helpers;
using PyRunner.Models;
using PyRunner.Services;
using PyRunner.ViewModels;
using PyRunner.Views;
using System.Diagnostics;

namespace PyRunner;

public sealed partial class MainWindow
{
    private async void OnSidebarSchedule(object? sender, ScriptListItemViewModel item)
    {
        if (_dialogInFlight) return;
        _dialogInFlight = true;
        try
        {
            await DialogHostHelper.ShowAfterFlyoutDismissAsync(sender as DependencyObject);
            await ShowScheduledTaskDialogAsync(item.Script, null);
        }
        catch (Exception ex) { DebugWriteUnexpected("SidebarSchedule", ex); }
        finally { _dialogInFlight = false; }
    }

    private async void OnScheduleAddRequested(Script? script) => await OpenScheduledTaskDialogGuardedAsync(script, null);
    private async void OnScheduleEditRequested(ScheduledTask task) => await OpenScheduledTaskDialogGuardedAsync(null, task);

    private async Task OpenScheduledTaskDialogGuardedAsync(Script? script, ScheduledTask? task)
    {
        if (_dialogInFlight) return;
        _dialogInFlight = true;
        try { await ShowScheduledTaskDialogAsync(script, task); }
        catch (Exception ex) { _scheduledTasksPage.ShowOperationError(); DebugWriteUnexpected("ScheduledTaskDialog", ex); }
        finally { _dialogInFlight = false; }
    }

    private async Task ShowScheduledTaskDialogAsync(Script? script, ScheduledTask? task)
    {
        var dialog = _scheduledTaskDialogFactory();
        PrepareDialog(dialog);
        dialog.Prepare(script, task);
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;
        _scheduledTasksPage.Refresh();
        _scheduleCoordinator.Rearm();
        _navigation.Navigate(ShellPage.ScheduledTasks);
    }

    private async void OnScheduleDeleteRequested(ScheduledTask task)
    {
        if (_dialogInFlight) return;
        _dialogInFlight = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = _localization["Schedule_Delete_Title"],
                Content = string.Format(_localization["Schedule_Delete_Content"], task.Name),
                PrimaryButtonText = _localization["Menu_Delete"],
                CloseButtonText = _localization["Button_Cancel"],
                DefaultButton = ContentDialogButton.Close,
            };
            PrepareDialog(dialog);
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                _scheduledTaskService.Delete(task.Id);
                _scheduledTasksPage.Refresh();
                _scheduleCoordinator.Rearm();
            }
        }
        catch (Exception ex) { _scheduledTasksPage.ShowOperationError(); DebugWriteUnexpected("ScheduledTaskDelete", ex); }
        finally { _dialogInFlight = false; }
    }

    private void OnSchedulesChanged() => _scheduleCoordinator.Rearm();
    private async void OnScheduleHistoryRequested(ScheduledTask task)
    {
        if (task.ScriptId is not int scriptId || !await CodeEditorViewModel.PrepareToSwitchAsync()) return;
        if (!_scriptListViewModel.SelectByScriptId(scriptId)) { _scheduledTasksPage.ShowOperationError(); return; }
        _historyViewModel.Load(scriptId);
        _navigation.Navigate(ShellPage.Runs);
    }
    private void OnScheduledTaskDue(ScheduledTask task) => StartScheduledTask(task, updateLastResult: false);
    private void OnScheduleRunNowRequested(ScheduledTask task) => StartScheduledTask(task, updateLastResult: true);

    private void StartScheduledTask(ScheduledTask task, bool updateLastResult)
    {
        try
        {
            var script = task.ScriptId is int scriptId ? _scriptService.GetById(scriptId) : null;
            if (script == null || !File.Exists(script.FilePath))
            {
                _scheduledTaskService.SetLastResult(task.Id, "missing");
                _scheduledTaskService.SetEnabled(task.Id, false);
                _scheduledTasksPage.Refresh();
                _scheduleCoordinator.Rearm();
                return;
            }
            if (_coordinator.IsRunning(script.Id))
            {
                _scheduledTaskService.SetLastResult(task.Id, "skipped");
                _scheduledTasksPage.Refresh();
                return;
            }
            var result = _coordinator.TryStart(script);
            if (!result.IsSuccess)
            {
                _scheduledTaskService.SetLastResult(
                    task.Id,
                    result.ErrorKey == "Run_ScriptNotFound" ? "missing" : "failed", result.ErrorKey);
                _scheduledTasksPage.Refresh();
                return;
            }
            if (updateLastResult)
                _scheduledTaskService.SetLastResult(task.Id, "running");
            _scheduledRunByScriptId[script.Id] = task.Id;
            CreateTab(result.ViewModel!, activate: false);
            _scheduledTasksPage.Refresh();
        }
        catch (Exception ex)
        {
            DebugWriteUnexpected("ScheduledTaskRun", ex);
            try { _scheduledTaskService.SetLastResult(task.Id, "failed", "Error_SaveFailed"); _scheduledTasksPage.Refresh(); }
            catch (Exception nested) { DebugWriteUnexpected("ScheduledTaskRunResult", nested); }
        }
    }

}
