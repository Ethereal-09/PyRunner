namespace PyRunner.Models;

/// <summary>运行记录（RunRecord 表）。时间戳为 UTC ISO-8601 字符串。</summary>
public sealed class RunRecord
{
    public int Id { get; set; }
    public int ScriptId { get; set; }
    public string? StartedAt { get; set; }
    public string? FinishedAt { get; set; }
    public int? ExitCode { get; set; }

    /// <summary>DB 文本状态：running/success/failed/killed（映射见 <see cref="RunStatusMapping"/>）。</summary>
    public string Status { get; set; } = "running";

    /// <summary>运行输出（服务端按 200KB 保留尾部截断）。</summary>
    public string? Output { get; set; }

    /// <summary>DB 状态 → UI 状态机枚举。</summary>
    public RunStatus RunStatus => RunStatusMapping.FromDbString(Status);
}
