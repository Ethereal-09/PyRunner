namespace PyRunner.Services;

/// <summary>构造传给 CreateProcessW 的 Python 命令行。</summary>
public static class PythonCommandLine
{
    /// <summary>
    /// Python 自身选项置于脚本前，用户脚本参数置于脚本后。
    /// Arguments 是用户录入的原始命令行片段，已由登记表单校验双引号配对。
    /// </summary>
    public static string Build(string interpreterPath, string scriptPath, string? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interpreterPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);

        var suffix = string.IsNullOrWhiteSpace(arguments)
            ? string.Empty
            : " " + arguments.Trim();

        return $"\"{interpreterPath}\" -u \"{scriptPath}\"{suffix}";
    }
}
