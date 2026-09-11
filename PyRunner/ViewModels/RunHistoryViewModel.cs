using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using PyRunner.Models;
using PyRunner.Services;

namespace PyRunner.ViewModels;

/// <summary>运行历史项展示模型（Phase E 交付 15）：时间转本地展示、输出摘要去 ANSI。</summary>
public sealed class RunHistoryItemViewModel
{
    public int RecordId { get; init; }

    /// <summary>开始时间（本地时间，如 08-19 14:03:22）。</summary>
    public string TimeText { get; init; } = string.Empty;

    /// <summary>状态文字（本地化：运行中/成功/失败/已终止）。</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary>状态机枚举（前景色经 RunStatusToBrushConverter）。</summary>
    public RunStatus Status { get; init; }

    /// <summary>退出码文字（未结束 = 「-」）。</summary>
    public string ExitCodeText { get; init; } = string.Empty;

    /// <summary>输出摘要（首个非空行，去 ANSI 转义、截断 120 字符）。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>完整输出（点开详情对话框展示）。</summary>
    public string FullOutput { get; init; } = string.Empty;

    public override string ToString() => $"{TimeText} {StatusText}";
}

/// <summary>
/// 运行历史面板 ViewModel（Phase E 交付 15）：终端下方展示当前脚本最近 10 条 RunRecord
/// （时间/状态/退出码/输出摘要；点开看完整输出；展示层转本地时间）；空历史空状态。
/// 刷新时机：选中脚本变化 + 运行状态机广播（MainWindow 接线）。
/// </summary>
public sealed partial class RunHistoryViewModel : ObservableObject
{
    /// <summary>输出摘要最大字符数。</summary>
    private const int SummaryMaxChars = 120;

    /// <summary>ANSI 转义序列（CSI/OSC/字符集切换）：摘要与详情展示前剥离。</summary>
    private static readonly Regex AnsiRegex = new(
        "\x1B(?:\\[[0-9;?]*[ -/]*[@-~]|\\][^\x07]*(?:\x07|\x1B\\\\)|[@-_][0-9:;<=>?@-Z\\\\-_|]*)",
        RegexOptions.Compiled);

    private readonly IRunRecordService _runRecordService;
    private readonly ILocalizationService _localization;

    /// <summary>当前展示的脚本 Id（null = 未选中脚本）。</summary>
    private int? _scriptId;

    public RunHistoryViewModel(IRunRecordService runRecordService, ILocalizationService localization)
    {
        _runRecordService = runRecordService;
        _localization = localization;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>窗口关闭时退订（服务层单例，避免事件持有瞬态 VM）。</summary>
    public void DetachLocalization() => _localization.LanguageChanged -= OnLanguageChanged;

    /// <summary>最近运行记录（按开始时间倒序，最多 10 条）。</summary>
    public ObservableCollection<RunHistoryItemViewModel> Records { get; } = new();

    /// <summary>空历史空状态（无选中脚本或该脚本无记录）。</summary>
    [ObservableProperty]
    private bool _showEmptyState;

    // ---- 展示属性（资源键） ----

    public string TitleText => _localization["History_Title"];
    public string EmptyText => _localization["History_Empty"];
    public string DetailTitleText => _localization["History_Detail_Title"];
    public string CloseText => _localization["Button_OK"];

    /// <summary>加载指定脚本的最近 10 条记录；scriptId=null → 空状态。</summary>
    public void Load(int? scriptId)
    {
        _scriptId = scriptId;
        Records.Clear();

        if (scriptId == null)
        {
            ShowEmptyState = true;
            return;
        }

        IReadOnlyList<RunRecord> recent;
        try
        {
            recent = _runRecordService.GetRecent(scriptId.Value, 10);
        }
        catch (Exception ex)
        {
            // 数据层故障：降级为空历史，不打断主链路
            recent = Array.Empty<RunRecord>();
            DebugWriteError("History: 运行记录加载失败", ex);
        }

        foreach (var record in recent)
            Records.Add(BuildItem(record));

        ShowEmptyState = Records.Count == 0;
    }

    private RunHistoryItemViewModel BuildItem(RunRecord record)
    {
        var status = record.RunStatus;
        var output = record.Output ?? string.Empty;

        return new RunHistoryItemViewModel
        {
            RecordId = record.Id,
            TimeText = ToLocalTimeText(record.StartedAt),
            Status = status,
            StatusText = StatusText(status),
            ExitCodeText = record.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "-",
            Summary = BuildSummary(output),
            // 终端程序在清屏或刷新输出时常会留下大量前导换行。
            // 详情视图从第一个实际字符开始，不改动后续输出的排版。
            FullOutput = AnsiRegex.Replace(output, string.Empty).TrimStart(),
        };
    }

    /// <summary>UTC ISO-8601 → 本地时间展示；解析失败回退原文。</summary>
    private static string ToLocalTimeText(string? utcIso)
    {
        if (string.IsNullOrWhiteSpace(utcIso)) return "-";
        return DateTime.TryParse(utcIso, CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? utc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : utcIso;
    }

    /// <summary>输出摘要：去 ANSI 后首个非空行，截断到 SummaryMaxChars。</summary>
    private static string BuildSummary(string output)
    {
        var clean = AnsiRegex.Replace(output, string.Empty);
        foreach (var rawLine in clean.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            return line.Length <= SummaryMaxChars
                ? line
                : line[..SummaryMaxChars] + "…";
        }

        return string.Empty;
    }

    private string StatusText(RunStatus status) => status switch
    {
        RunStatus.Running => _localization["StatusBadge_Running"],
        RunStatus.Success => _localization["StatusBadge_Success"],
        RunStatus.Failed => _localization["StatusBadge_Failed"],
        RunStatus.Killed => _localization["StatusBadge_Killed"],
        _ => _localization["StatusBadge_NotRun"],
    };

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(DetailTitleText));
        OnPropertyChanged(nameof(CloseText));
        Load(_scriptId); // 状态文字本地化重建
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugWriteError(string context, Exception ex)
    {
#if DEBUG
        DebugLog.WriteLine($"{context}（{ex.Message}）");
#endif
    }
}
