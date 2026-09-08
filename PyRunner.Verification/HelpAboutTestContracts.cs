namespace PyRunner.Services;

// Verification 独立于 WinUI 运行时，因此只声明 HelpAboutViewModel 所需的纯契约。
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

public interface ILocalizationService
{
    string CurrentLanguage { get; }
    string this[string key] { get; }
    void SetLanguage(string languageCode);
    event Action? LanguageChanged;
}

public interface IAppMetadataService
{
    AppMetadata GetSnapshot();
}

public interface IClipboardService
{
    void CopyText(string text);
}

public interface IExternalLinkService
{
    Task<bool> OpenAsync(Uri uri);
}

public sealed record ProductLinksOptions(
    Uri? ProjectHomepage = null,
    Uri? IssueTracker = null,
    Uri? ReleasesPage = null,
    Uri? AuthorHomepage = null,
    Uri? LicensePage = null,
    Uri? UpdateFeed = null);
