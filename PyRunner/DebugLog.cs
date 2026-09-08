#if DEBUG
namespace PyRunner;

/// <summary>
/// 仅 Debug 构建生效的临时诊断日志：写入 %TEMP%\pyrunner-debug.log。
/// 用于定位 WebView2 ⇄ ConPTY 链路问题，Release 构建完全移除。
/// </summary>
internal static class DebugLog
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "pyrunner-debug.log");
    private static readonly object Sync = new();

    public static void WriteLine(string message)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 诊断日志本身不允许影响主流程
        }
    }
}
#endif
