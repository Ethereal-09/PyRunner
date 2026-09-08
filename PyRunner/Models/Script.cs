namespace PyRunner.Models;

/// <summary>脚本登记记录（Script 表）。时间戳为 UTC ISO-8601 字符串。</summary>
public sealed class Script
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>.py 绝对路径（UNIQUE）。</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>分类；空值由 DB 默认 '未分类' 兜底（PRD §7.1）。</summary>
    public string? Category { get; set; }

    /// <summary>逗号分隔标签。</summary>
    public string? Tags { get; set; }

    public string? Description { get; set; }

    /// <summary>指定解释器；null = 使用默认解释器。</summary>
    public int? InterpreterId { get; set; }

    /// <summary>启动参数。</summary>
    public string? Arguments { get; set; }

    /// <summary>工作目录；null = 脚本所在目录。</summary>
    public string? WorkingDirectory { get; set; }

    public bool IsFavorite { get; set; }

    public string? CreatedAt { get; set; }
    public string? UpdatedAt { get; set; }
}
