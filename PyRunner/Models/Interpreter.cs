namespace PyRunner.Models;

/// <summary>Python 解释器登记（Interpreter 表）。</summary>
public sealed class Interpreter
{
    public int Id { get; set; }

    /// <summary>显示名（如 "Python 3.13.0"）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>python.exe 绝对路径（UNIQUE；禁止 pythonw.exe——它没有 stdin/stdout）。</summary>
    public string ExecutablePath { get; set; } = string.Empty;

    /// <summary>自动检测到的真实版本号（--version 输出解析）。</summary>
    public string? Version { get; set; }

    public bool IsDefault { get; set; }

    public string? CreatedAt { get; set; }
}
