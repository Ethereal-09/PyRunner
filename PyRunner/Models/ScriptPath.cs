namespace PyRunner.Models;

/// <summary>脚本存放路径（ScriptPath 表）。</summary>
public sealed class ScriptPath
{
    public int Id { get; set; }

    /// <summary>目录绝对路径（UNIQUE）。</summary>
    public string Path { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string? CreatedAt { get; set; }
}
