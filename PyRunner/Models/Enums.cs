namespace PyRunner.Models;

/// <summary>
/// 运行状态（UI 状态机）。
/// DB 映射（RunRecord.Status 字段，TEXT）：
///   Running → "running"、Success → "success"、Failed → "failed"、Killed → "killed"；
///   NotRun 为纯 UI 态（无对应运行记录，不落库）。
/// 建表默认值 'running'（见 Data/Migrations/001_initial.sql）。
/// </summary>
public enum RunStatus
{
    NotRun,
    Running,
    Success,
    Failed,
    Killed,
}

/// <summary>RunStatus ⇄ DB 字符串映射（DB 只存 running/success/failed/killed）。</summary>
public static class RunStatusMapping
{
    public static string ToDbString(RunStatus status) => status switch
    {
        RunStatus.Running => "running",
        RunStatus.Success => "success",
        RunStatus.Failed => "failed",
        RunStatus.Killed => "killed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "NotRun 不落库"),
    };

    public static RunStatus FromDbString(string? status) => status switch
    {
        "running" => RunStatus.Running,
        "success" => RunStatus.Success,
        "failed" => RunStatus.Failed,
        "killed" => RunStatus.Killed,
        _ => UnknownStatusFallback(status),
    };

    /// <summary>未知状态值回退 Failed（不再静默 NotRun）并写 Debug 日志，便于发现脏数据。</summary>
    private static RunStatus UnknownStatusFallback(string? status)
    {
        System.Diagnostics.Debug.WriteLine(
            $"RunStatusMapping: 未知 DB 状态 '{status ?? "(null)"}'，回退 Failed");
        return RunStatus.Failed;
    }
}
