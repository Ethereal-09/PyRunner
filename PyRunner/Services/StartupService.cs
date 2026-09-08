using Microsoft.Win32;

namespace PyRunner.Services;

/// <summary>管理当前用户的 Windows 登录启动项。</summary>
public interface IStartupService
{
    /// <summary>启用或移除 PyRunner 的当前用户启动项；失败时抛出异常供设置页友好提示。</summary>
    void SetEnabled(bool enabled);
}

/// <summary>
/// 未打包自包含部署的开机自启实现。仅写 HKCU，不需要管理员权限；
/// 注册值始终更新为当前 exe 路径，应用移动后重新勾选即可修复。
/// </summary>
public sealed class StartupService : IStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PyRunner";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows 启动项注册表键不可用");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            throw new InvalidOperationException("无法确定 PyRunner 可执行文件路径");

        key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
    }
}
