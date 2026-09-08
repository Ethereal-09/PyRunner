using System.Diagnostics;

namespace PyRunner.Services;

/// <summary>
/// Python 解释器发现（供 InterpreterService 自动扫描候选复用）。
/// 行为复刻 Terminal\PythonLocator.cs 的 PATH 逻辑（该文件本阶段冻结，不改一行），
/// 额外增加 py 启动器（py -0）列举；过滤 WindowsApps 桩与 pythonw.exe。
/// </summary>
internal static class PythonDiscovery
{
    private const string StoreStubMarker = "WindowsApps";
    private const string EnvVarName = "PYRUNNER_PYTHON";

    /// <summary>
    /// 收集候选 python.exe 绝对路径（去重、按发现优先级排序）：
    /// 1) PYRUNNER_PYTHON 环境变量；2) PATH 扫描（过滤 WindowsApps）；3) py -0 列举。
    /// </summary>
    public static IReadOnlyList<string> ScanCandidates()
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path.Trim().Trim('"'));
                if (!File.Exists(full)) return;
                var fileName = Path.GetFileName(full);
                // pythonw.exe 无 stdin/stdout，禁止使用（PRD §7.2）
                if (fileName.Equals("pythonw.exe", StringComparison.OrdinalIgnoreCase)) return;
                if (!fileName.Equals("python.exe", StringComparison.OrdinalIgnoreCase)) return;
                if (seen.Add(full)) candidates.Add(full);
            }
            catch
            {
                // 非法路径，忽略
            }
        }

        // 1. 环境变量后门
        Add(Environment.GetEnvironmentVariable(EnvVarName));

        // 2. PATH 扫描（与 PythonLocator 相同的过滤规则）
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var rawDir in pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim();
            if (dir.Length == 0) continue;
            if (dir.Contains(StoreStubMarker, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                Add(Path.Combine(dir, "python.exe"));
            }
            catch
            {
                // 忽略非法路径条目
            }
        }

        // 3. py 启动器列举（py -0 输出形如 " -3.13-64 * C:\...\python.exe"）
        foreach (var path in ListFromPyLauncher())
            Add(path);

        return candidates;
    }

    /// <summary>调用 py.exe -0 列举已注册解释器路径；失败/超时返回空。</summary>
    private static IEnumerable<string> ListFromPyLauncher()
    {
        var results = new List<string>();
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "py.exe",
                Arguments = "-0",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            if (!process.Start()) return results;

            // 事件式异步读取（BeginOutputReadLine 后再 WaitForExit）：
            // 消除同步 ReadToEnd + WaitForExit 在大输出下的管道满死锁模式
            var collector = ProcessOutputCollector.Attach(process);

            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
                return results;
            }
            process.WaitForExit(); // 确保异步读取线程排空缓冲

            var output = collector.GetText();
            foreach (var line in output.Split('\n'))
            {
                // 提取行内形如 X:\...\python.exe 的绝对路径
                var trimmed = line.Trim().TrimEnd('\r');
                var idx = trimmed.IndexOf(":\\", StringComparison.Ordinal);
                if (idx <= 0) continue;

                var start = idx - 1;
                while (start > 0 && !char.IsWhiteSpace(trimmed[start - 1])) start--;
                var candidate = trimmed[start..].Trim().TrimEnd('"');
                if (candidate.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase))
                    results.Add(candidate);
            }
        }
        catch
        {
            // py.exe 不存在或执行失败：静默跳过该来源
        }

        return results;
    }
}
