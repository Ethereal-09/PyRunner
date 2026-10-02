using CommunityToolkit.Mvvm.ComponentModel;
using PyRunner.Models;
using PyRunner.Services;
using System.Collections.ObjectModel;
using System.Globalization;

namespace PyRunner.ViewModels;

public sealed class ScheduledTaskItemViewModel
{
    public required ScheduledTask Task { get; init; }
    public string Name => Task.Name;
    public string ScriptName => Task.ScriptId == null ? $"{Task.ScriptName} · !" : Task.ScriptName;
    public string RuleText { get; init; } = string.Empty;
    public string NextRunText { get; init; } = string.Empty;
    public string LastResultText { get; init; } = string.Empty;
    public string RunNowText { get; init; } = string.Empty;
    public string EditText { get; init; } = string.Empty;
    public string DeleteText { get; init; } = string.Empty;
    public string EnableText { get; init; } = string.Empty;
    public string MoreText { get; init; } = string.Empty;
    public string DiagnosticText { get; init; } = string.Empty;
    public string HistoryText { get; init; } = string.Empty;
    public bool Enabled { get => Task.Enabled; set => Task.Enabled = value; }
    public bool CanEnable => Task.ScriptId != null;
}

public sealed partial class ScheduledTasksViewModel : ObservableObject
{
    private readonly IScheduledTaskService _tasks;
    private readonly ILocalizationService _localization;

    public ScheduledTasksViewModel(IScheduledTaskService tasks, ILocalizationService localization)
    {
        _tasks = tasks;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<ScheduledTaskItemViewModel> Items { get; } = new();
    [ObservableProperty] private bool _showEmptyState;
    [ObservableProperty] private bool _hasLoadError;
    public string LoadErrorText => _localization["List_LoadError"];
    public string RetryText => _localization["Button_Retry"];
    public string ScopeText => _localization["Schedule_Scope"];
    public string OperationErrorText => _localization["Schedule_OperationError"];

    public string TitleText => _localization["Schedule_Title"];
    public string AddText => _localization["Schedule_Add"];
    public string EmptyText => _localization["Schedule_Empty"];
    public string ScriptHeaderText => _localization["Schedule_Column_Script"];
    public string RuleHeaderText => _localization["Schedule_Column_Rule"];
    public string NextHeaderText => _localization["Schedule_Column_Next"];
    public string ResultHeaderText => _localization["Schedule_Column_Result"];
    public string RunNowText => _localization["Schedule_RunNow"];
    public string EditText => _localization["Button_Edit"];
    public string DeleteText => _localization["Menu_Delete"];

    public void Load()
    {
        Items.Clear();
        HasLoadError = false;
        IReadOnlyList<ScheduledTask> tasks;
        try { tasks = _tasks.GetAll(); }
        catch { tasks = Array.Empty<ScheduledTask>(); HasLoadError = true; }
        foreach (var task in tasks)
        {
            Items.Add(new ScheduledTaskItemViewModel
            {
                Task = task,
                RuleText = FormatRule(task),
                NextRunText = FormatTime(task.NextRunAtUtc),
                LastResultText = FormatResult(task.LastResult),
                RunNowText = RunNowText,
                EditText = EditText,
                DeleteText = DeleteText,
                HistoryText = _localization["Nav_Runs"],
                EnableText = string.Format(_localization["Schedule_EnableTask"], task.Name),
                MoreText = string.Format(_localization["Schedule_MoreTask"], task.Name),
                DiagnosticText = string.Join(" · ", new[] { FormatTime(task.LastRunAtUtc),
                    string.IsNullOrEmpty(task.LastErrorKey) ? FormatResult(task.LastResult) : _localization[task.LastErrorKey] }),
            });
        }
        ShowEmptyState = Items.Count == 0 && !HasLoadError;
    }

    private string FormatRule(ScheduledTask task)
    {
        if (!DateTime.TryParse(task.StartAtLocal, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) return "-";
        return task.ScheduleType switch
        {
            ScheduleTypes.Once => string.Format(_localization["Schedule_Rule_Once"], start.ToString("yyyy-MM-dd HH:mm")),
            ScheduleTypes.Daily => string.Format(_localization["Schedule_Rule_Daily"], start.ToString("HH:mm")),
            ScheduleTypes.Weekly => string.Format(_localization["Schedule_Rule_Weekly"], Weekdays(task.DaysOfWeek), start.ToString("HH:mm")),
            ScheduleTypes.Interval => string.Format(_localization["Schedule_Rule_Interval"], task.IntervalMinutes),
            _ => task.ScheduleType,
        };
    }

    private string Weekdays(int mask)
    {
        var names = new[] { _localization["Weekday_Sun"], _localization["Weekday_Mon"], _localization["Weekday_Tue"], _localization["Weekday_Wed"], _localization["Weekday_Thu"], _localization["Weekday_Fri"], _localization["Weekday_Sat"] };
        return string.Join("、", Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => names[i]));
    }

    private static string FormatTime(string? utc) => DateTimeOffset.TryParse(utc, out var value) ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "-";
    private string FormatResult(string? result) => result switch
    {
        "running" => _localization["StatusBadge_Running"], "success" => _localization["StatusBadge_Success"],
        "failed" => _localization["StatusBadge_Failed"], "killed" => _localization["StatusBadge_Killed"],
        "skipped" => _localization["Schedule_Result_Skipped"], "missing" => _localization["Schedule_Result_Missing"], _ => "-",
    };

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(TitleText)); OnPropertyChanged(nameof(AddText)); OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ScriptHeaderText)); OnPropertyChanged(nameof(RuleHeaderText)); OnPropertyChanged(nameof(NextHeaderText));
        OnPropertyChanged(nameof(ResultHeaderText)); OnPropertyChanged(nameof(RunNowText)); OnPropertyChanged(nameof(EditText)); OnPropertyChanged(nameof(DeleteText));
        OnPropertyChanged(nameof(LoadErrorText)); OnPropertyChanged(nameof(RetryText)); OnPropertyChanged(nameof(ScopeText));
        Load();
    }
    public void DetachLocalization() => _localization.LanguageChanged -= OnLanguageChanged;
}
