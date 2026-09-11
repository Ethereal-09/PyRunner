using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PyRunner.Models;
using PyRunner.Services;
using System.Globalization;

namespace PyRunner.Views;

public sealed partial class ScheduledTaskEditDialog : ContentDialog
{
    private readonly IScheduledTaskService _tasks; private readonly IScriptService _scripts; private readonly ILocalizationService _localization; private int _editingId;
    private sealed class Option<T>(T value, string text) { public T Value { get; } = value; public string Text { get; } = text; public override string ToString() => Text; }
    public ScheduledTaskEditDialog(IScheduledTaskService tasks, IScriptService scripts, ILocalizationService localization)
    { _tasks = tasks; _scripts = scripts; _localization = localization; InitializeComponent(); Closing += OnClosing; ApplyText(); }

    public void Prepare(Script? preferredScript, ScheduledTask? existing = null)
    {
        var scripts = _scripts.GetAll().Select(s => new Option<Script>(s, s.Name)).ToList(); ScriptCombo.ItemsSource = scripts;
        var types = new[] { new Option<string>(ScheduleTypes.Once, _localization["Schedule_Type_Once"]), new Option<string>(ScheduleTypes.Daily, _localization["Schedule_Type_Daily"]), new Option<string>(ScheduleTypes.Weekly, _localization["Schedule_Type_Weekly"]), new Option<string>(ScheduleTypes.Interval, _localization["Schedule_Type_Interval"]) };
        TypeCombo.ItemsSource = types;
        var local = DateTime.Now.AddMinutes(5); if (existing != null && DateTime.TryParse(existing.StartAtLocal, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) local = parsed;
        _editingId = existing?.Id ?? 0; NameBox.Text = existing?.Name ?? preferredScript?.Name ?? string.Empty; DatePicker.Date = new DateTimeOffset(local.Date); TaskTimePicker.Time = local.TimeOfDay;
        EnabledToggle.IsOn = existing?.Enabled ?? true; IntervalBox.Value = existing?.IntervalMinutes > 0 ? existing.IntervalMinutes : 60;
        var scriptId = existing?.ScriptId ?? preferredScript?.Id; ScriptCombo.SelectedItem = scripts.FirstOrDefault(o => o.Value.Id == scriptId) ?? scripts.FirstOrDefault();
        TypeCombo.SelectedItem = types.First(o => o.Value == (existing?.ScheduleType ?? ScheduleTypes.Daily)); SetWeekdays(existing?.DaysOfWeek ?? (1 << (int)DateTime.Now.DayOfWeek)); ErrorText.Text = string.Empty;
        Title = _editingId == 0 ? _localization["Schedule_Add"] : _localization["Schedule_Edit"];
    }
    private void ApplyText()
    {
        PrimaryButtonText = _localization["Button_Save"]; CloseButtonText = _localization["Button_Cancel"]; NameLabel.Text = _localization["Schedule_Field_Name"];
        ScriptLabel.Text = _localization["Schedule_Field_Script"]; TypeLabel.Text = _localization["Schedule_Field_Type"]; DateLabel.Text = _localization["Schedule_Field_StartDate"];
        TimeLabel.Text = _localization["Schedule_Field_Time"]; WeekdayLabel.Text = _localization["Schedule_Field_Weekdays"]; IntervalLabel.Text = _localization["Schedule_Field_Interval"]; EnabledToggle.Header = _localization["Schedule_Field_Enabled"];
    }
    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TypeCombo.SelectedItem is not Option<string> option) return; WeekdayPanel.Visibility = option.Value == ScheduleTypes.Weekly ? Visibility.Visible : Visibility.Collapsed;
        IntervalPanel.Visibility = option.Value == ScheduleTypes.Interval ? Visibility.Visible : Visibility.Collapsed; DateLabel.Visibility = option.Value is ScheduleTypes.Once or ScheduleTypes.Interval ? Visibility.Visible : Visibility.Collapsed; DatePicker.Visibility = DateLabel.Visibility;
    }
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (args.Result != ContentDialogResult.Primary) return;
        try
        {
            if (ScriptCombo.SelectedItem is not Option<Script> script || TypeCombo.SelectedItem is not Option<string> type) throw new ValidationException("Schedule_Error_ScriptRequired");
            var date = DatePicker.Date?.Date ?? DateTimeOffset.Now.Date; var local = date.Add(TaskTimePicker.Time);
            var task = new ScheduledTask { Id = _editingId, Name = NameBox.Text.Trim(), ScriptId = script.Value.Id, ScriptName = script.Value.Name, ScriptPath = script.Value.FilePath, ScheduleType = type.Value, StartAtLocal = local.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), DaysOfWeek = GetWeekdays(), IntervalMinutes = double.IsNaN(IntervalBox.Value) ? 0 : (int)IntervalBox.Value, Enabled = EnabledToggle.IsOn };
            if (_editingId == 0) _tasks.Add(task); else _tasks.Update(task);
        }
        catch (ValidationException ex) { ErrorText.Text = _localization[ex.LocalizationKey]; args.Cancel = true; }
        catch { ErrorText.Text = _localization["Error_SaveFailed"]; args.Cancel = true; }
    }
    private int GetWeekdays() { var boxes = new[] { Sun, Mon, Tue, Wed, Thu, Fri, Sat }; return Enumerable.Range(0, 7).Where(i => boxes[i].IsChecked == true).Sum(i => 1 << i); }
    private void SetWeekdays(int mask) { var boxes = new[] { Sun, Mon, Tue, Wed, Thu, Fri, Sat }; for (var i = 0; i < boxes.Length; i++) boxes[i].IsChecked = (mask & (1 << i)) != 0; }
}
