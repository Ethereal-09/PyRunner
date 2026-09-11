namespace PyRunner.Models;

/// <summary>本地定时任务。时间规则按本机时区保存，NextRunAtUtc 用于可靠排序和触发。</summary>
public sealed class ScheduledTask
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? ScriptId { get; set; }
    public string ScriptName { get; set; } = string.Empty;
    public string ScriptPath { get; set; } = string.Empty;
    public string ScheduleType { get; set; } = "daily";
    public string StartAtLocal { get; set; } = string.Empty;
    public int DaysOfWeek { get; set; }
    public int IntervalMinutes { get; set; }
    public string? NextRunAtUtc { get; set; }
    public string? LastRunAtUtc { get; set; }
    public string? LastResult { get; set; }
    public bool Enabled { get; set; } = true;
    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}

public static class ScheduleTypes
{
    public const string Once = "once";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Interval = "interval";
}
