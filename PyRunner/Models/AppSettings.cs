namespace PyRunner.Models;

/// <summary>
/// 应用设置（强类型，持久化到 %LOCALAPPDATA%\PyRunner\settings.json，PRD §7.5 + Language 键修正）。
/// </summary>
public sealed class AppSettings
{
    /// <summary>侧栏宽度（px），默认 290。</summary>
    public int SidebarWidth { get; set; } = 290;

    /// <summary>主界面布局版本；升级时可一次性迁移旧版测试/设计阶段留下的尺寸。</summary>
    public int UiLayoutVersion { get; set; }

    /// <summary>文件树展开状态序列化（Phase C 使用）。</summary>
    public string SavedTreeExpanded { get; set; } = string.Empty;

    /// <summary>界面语言：zh-CN / en-US；默认按 CultureInfo.CurrentUICulture。</summary>
    public string Language { get; set; } =
        System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en-US";

    /// <summary>界面主题：Dark / Light。默认深色以保持既有用户界面。</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>运行结束后自动清空终端输出。</summary>
    public bool TerminalAutoClear { get; set; }

    /// <summary>脚本失败时弹窗提醒。</summary>
    public bool NotifyOnFail { get; set; } = true;

    /// <summary>输出实时刷新（关闭则结束后一次性输出）。</summary>
    public bool LiveFlush { get; set; } = true;

    /// <summary>开机自启动。</summary>
    public bool AutoStart { get; set; }

    /// <summary>启动时自动扫描并同步已配置的脚本目录。</summary>
    public bool AutoRefreshScripts { get; set; } = true;

    /// <summary>首次启动引导是否已经完整完成。</summary>
    public bool FirstRunCompleted { get; set; }

    /// <summary>已经完成的引导版本；功能升级时可安全触发新版引导。</summary>
    public int FirstRunVersion { get; set; }

    /// <summary>最后一次真实更新请求的时间；用于启动时24小时节流。</summary>
    public DateTimeOffset? LastUpdateCheckAttemptUtc { get; set; }

    /// <summary>最后一次成功解析的GitHub Release；失败请求不得覆盖。</summary>
    public UpdateCheckCache? LastSuccessfulUpdateCheck { get; set; }
}
