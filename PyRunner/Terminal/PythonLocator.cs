namespace PyRunner.Terminal;

/// <summary>
/// 定位真实的 python.exe。
/// 优先级：环境变量后门 PYRUNNER_PYTHON → PATH 扫描（过滤 WindowsApps 桩）→ 交给系统解析。
/// </summary>
public static class PythonLocator
{
    /// <summary>环境变量后门：显式指定 python.exe 完整路径（存在且文件有效则优先）。</summary>
    private const string EnvVarName = "PYRUNNER_PYTHON";

    private const string StoreStubMarker = "WindowsApps";

    public static string ResolvePython()
    {
        // 1. 环境变量后门：存在且指向真实文件则直接使用
        var overridePath = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            overridePath = overridePath.Trim().Trim('"');
            try
            {
                if (File.Exists(overridePath))
                    return overridePath;
            }
            catch
            {
                // 非法路径字符，回退 PATH 扫描
            }
#if DEBUG
            DebugLog.WriteLine($"PythonLocator: {EnvVarName}={overridePath} 无效，回退 PATH 扫描");
#endif
        }

        // 2. 扫描 PATH（过滤 WindowsApps 下的 Microsoft Store 桩程序）
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var rawDir in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim();
            if (dir.Length == 0) continue;
            if (dir.Contains(StoreStubMarker, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var candidate = Path.Combine(dir, "python.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 忽略非法路径条目
            }
        }

        // 3. 最后交给系统解析
        return "python.exe";
    }
}
