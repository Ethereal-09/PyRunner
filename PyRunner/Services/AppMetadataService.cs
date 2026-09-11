using System.Reflection;
using System.Runtime.InteropServices;

namespace PyRunner.Services;

/// <summary>帮助页所需的非敏感、只读运行信息。</summary>
public sealed record AppMetadata(
    string Version,
    string BuildNumber,
    string Architecture,
    string ReleaseChannel,
    string AuthorAccount,
    string AuthorIdentity,
    string DotNetRuntime,
    string WindowsAppSdk,
    string OperatingSystem,
    string PythonVersion,
    string WebView2Runtime,
    string TerminalComponents,
    string License,
    string UpdateChannel);

public interface IAppMetadataService
{
    AppMetadata GetSnapshot();
}

/// <summary>
/// 从构建元数据、已加载运行时和默认解释器读取信息。这里刻意不返回用户目录、
/// 解释器路径、环境变量等内容，复制软件信息时不会泄露本机私人路径。
/// </summary>
public sealed class AppMetadataService : IAppMetadataService
{
    private readonly IInterpreterService _interpreterService;

    public AppMetadataService(IInterpreterService interpreterService) =>
        _interpreterService = interpreterService;

    public AppMetadata GetSnapshot()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        // SDK 从 PyRunner.csproj 的 <Version> 生成该属性。显示与更新比较都读取它，
        // 避免把用于程序集绑定的 AssemblyVersion 或含构建号的 FileVersion 当产品版本。
        var productVersion = ProductVersionParser.NormalizeInformationalVersion(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(item => item.Key, item => item.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        string webView2Runtime;
        try
        {
            webView2Runtime = Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .GetAvailableBrowserVersionString()
                .Split(' ')[0];
        }
        catch
        {
            webView2Runtime = "unavailable";
        }

        string pythonVersion;
        try
        {
            pythonVersion = _interpreterService.GetDefault()?.Version?.Trim() ?? string.Empty;
        }
        catch
        {
            // 帮助页必须可离线打开；数据库或解释器读取失败只降级该字段。
            pythonVersion = string.Empty;
        }

        return new AppMetadata(
            Version: productVersion ?? string.Empty,
            BuildNumber: metadata.GetValueOrDefault("BuildNumber", "0"),
            Architecture: RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            ReleaseChannel: metadata.GetValueOrDefault("ReleaseChannel", "stable"),
            AuthorAccount: metadata.GetValueOrDefault("AuthorAccount", "@Ethereal-09"),
            AuthorIdentity: metadata.GetValueOrDefault("AuthorIdentity", "independent_developer"),
            DotNetRuntime: RuntimeInformation.FrameworkDescription,
            WindowsAppSdk: $"Windows App SDK {metadata.GetValueOrDefault("WindowsAppSdkVersion", "1.5")}",
            OperatingSystem: RuntimeInformation.OSDescription.Trim(),
            PythonVersion: pythonVersion,
            WebView2Runtime: webView2Runtime,
            TerminalComponents: "xterm.js · ConPTY",
            License: metadata.GetValueOrDefault("License", string.Empty),
            UpdateChannel: metadata.GetValueOrDefault("UpdateChannel", string.Empty));
    }
}
