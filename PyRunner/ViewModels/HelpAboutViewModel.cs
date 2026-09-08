using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyRunner.Models;
using PyRunner.Services;

namespace PyRunner.ViewModels;

public sealed partial class HelpAboutViewModel : ObservableObject, IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly IAppMetadataService _metadataService;
    private readonly IClipboardService _clipboard;
    private readonly IExternalLinkService _externalLinks;
    private readonly UpdateCheckCoordinator _updateCoordinator;
    private readonly IUpdateStateStore _updateState;
    private readonly ProductLinksOptions _links;
    private bool _disposed;

    [ObservableProperty] private AppMetadata metadata = null!;
    [ObservableProperty] private bool isLocalHelpOpen;
    [ObservableProperty] private string localHelpTitle = string.Empty;
    [ObservableProperty] private string localHelpBody = string.Empty;
    [ObservableProperty] private bool isCopyFeedbackVisible;
    [ObservableProperty] private bool copySucceeded = true;
    [ObservableProperty] private bool isCheckingUpdates;
    [ObservableProperty] private UpdateCheckResult? updateResult;
    [ObservableProperty] private UpdateCheckResult? lastSuccessfulUpdateResult;
    [ObservableProperty] private string operationMessage = string.Empty;

    public IRelayCommand<string> NavigateToSettingsCommand { get; }
    public IRelayCommand<string> ShowLocalHelpCommand { get; }
    public IRelayCommand CloseLocalHelpCommand { get; }
    public IAsyncRelayCommand CopySoftwareInfoCommand { get; }
    public IAsyncRelayCommand CheckUpdatesCommand { get; }
    public IAsyncRelayCommand DownloadUpdateCommand { get; }
    public IAsyncRelayCommand<string> OpenExternalLinkCommand { get; }

    public event EventHandler<HelpSettingsSection>? SettingsNavigationRequested;

    public HelpAboutViewModel(
        ILocalizationService localization,
        IAppMetadataService metadataService,
        IClipboardService clipboard,
        IExternalLinkService externalLinks,
        UpdateCheckCoordinator updateCoordinator,
        IUpdateStateStore updateState,
        ProductLinksOptions links)
    {
        _localization = localization;
        _metadataService = metadataService;
        _clipboard = clipboard;
        _externalLinks = externalLinks;
        _updateCoordinator = updateCoordinator;
        _updateState = updateState;
        _links = links;
        metadata = _metadataService.GetSnapshot();

        NavigateToSettingsCommand = new RelayCommand<string>(NavigateToSettings);
        ShowLocalHelpCommand = new RelayCommand<string>(ShowLocalHelp);
        CloseLocalHelpCommand = new RelayCommand(() => IsLocalHelpOpen = false);
        CopySoftwareInfoCommand = new AsyncRelayCommand(CopySoftwareInfoAsync, () => !IsCopyFeedbackVisible);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, () => !IsCheckingUpdates);
        DownloadUpdateCommand = new AsyncRelayCommand(DownloadUpdateAsync, () => CanOpenDownload);
        OpenExternalLinkCommand = new AsyncRelayCommand<string>(OpenExternalLinkAsync);

        _localization.LanguageChanged += OnLanguageChanged;
        _updateState.Changed += OnUpdateStateChanged;
        ApplyUpdateState(_updateState.Current);
    }

    public string PageTitle => L("Help_Title");
    public string PageSubtitle => L("Help_Subtitle");
    public string ProductDescription => L("Help_ProductDescription");
    private string DisplayedProductVersion => string.IsNullOrWhiteSpace(Metadata.Version)
        ? L("Help_UnknownVersion")
        : Metadata.Version;
    public string VersionLine => string.Format(L("Help_VersionLine"), DisplayedProductVersion, Metadata.BuildNumber, Metadata.Architecture, LocalizeChannel(Metadata.ReleaseChannel));
    public string AuthorTitle => L("Help_Author");
    public string AuthorIdentity => string.Equals(Metadata.AuthorIdentity, "independent_developer", StringComparison.OrdinalIgnoreCase)
        ? L("Help_AuthorIdentity")
        : Metadata.AuthorIdentity;
    public string AuthorAccount => Metadata.AuthorAccount;
    public string AuthorBio => L("Help_AuthorBio");
    public string SoftwareTitle => L("Help_SoftwareInfo");
    public string RuntimeInfo => string.Format(L("Help_RuntimeInfo"), Metadata.DotNetRuntime, Metadata.WindowsAppSdk);
    public string PythonInfo => string.Format(L("Help_PythonInfo"), string.IsNullOrWhiteSpace(Metadata.PythonVersion) ? L("Help_NotConfigured") : $"Python {Metadata.PythonVersion}");
    public string OsInfo => string.Format(L("Help_OsInfo"), Metadata.OperatingSystem);
    public string TerminalInfo => string.Format(L("Help_TerminalInfo"), Metadata.WebView2Runtime, Metadata.TerminalComponents);
    public string LicenseInfo => string.IsNullOrWhiteSpace(Metadata.License) ? string.Empty : string.Format(L("Help_LicenseInfo"), Metadata.License);
    public string UpdateChannelInfo => string.IsNullOrWhiteSpace(Metadata.UpdateChannel) ? string.Empty : string.Format(L("Help_UpdateChannelInfo"), Metadata.UpdateChannel);
    public string QuickHelpTitle => L("Help_QuickHelp");
    public string AddFolderTitle => L("Help_AddFolder");
    public string AddFolderDescription => L("Help_AddFolderDescription");
    public string InterpreterTitle => L("Help_Interpreter");
    public string InterpreterDescription => L("Help_InterpreterDescription");
    public string RunGuideTitle => L("Help_RunGuide");
    public string RunGuideDescription => L("Help_RunGuideDescription");
    public string FaqTitle => L("Help_Faq");
    public string FaqDescription => L("Help_FaqDescription");
    public string CopyInfoText => L("Help_CopyInfo");
    public string CopyFeedbackText => L(CopySucceeded ? "Help_Copied" : "Help_CopyFailed");
    public string OnlineResourcesUnavailable => L("Help_OnlineResourcesUnavailable");
    public string CopyrightText => string.Format(L("Help_Copyright"), DateTime.Now.Year);
    public string CheckUpdatesText => IsCheckingUpdates ? L("Help_CheckingUpdates") : L("Help_CheckUpdates");
    public string DownloadUpdateText => L("Help_GoToDownload");
    public string ReleaseNotesTitle => L("Help_UpdateNotesTitle");

    public bool CanCheckUpdates => _links.UpdateFeed is not null;
    public bool HasProjectLink => _links.ProjectHomepage is not null;
    public bool HasIssueLink => _links.IssueTracker is not null;
    public bool HasReleasesLink => _links.ReleasesPage is not null;
    public bool HasAuthorLink => _links.AuthorHomepage is not null;
    public bool HasLicenseLink => _links.LicensePage is not null;
    public bool HasExternalLinks => HasProjectLink || HasIssueLink || HasReleasesLink || HasAuthorLink || HasLicenseLink;
    public bool ShowUnavailableResources => !HasExternalLinks && !CanCheckUpdates;
    public string ProjectHomepageText => L("Help_ProjectHomepage");
    public string IssueTrackerText => L("Help_IssueTracker");
    public string ReleasesPageText => L("Help_ReleasesPage");
    public string AuthorHomepageText => L("Help_AuthorHomepage");
    public string LicensePageText => L("Help_LicensePage");

    public bool ShowUpdateStatus => IsCheckingUpdates || UpdateResult is { Status: not UpdateCheckStatus.NotChecked };
    public string UpdateStatusText => IsCheckingUpdates ? L("Help_CheckingUpdates") : FormatUpdateResult(UpdateResult);
    public UpdateCheckResult? DisplayedRelease =>
        UpdateResult?.IsSuccessful == true ? UpdateResult : LastSuccessfulUpdateResult;
    public bool ShowUpdateDetails => DisplayedRelease?.Status == UpdateCheckStatus.UpdateAvailable;
    public string UpdateVersionText => string.Format(L("Help_UpdateVersion"), DisplayedRelease?.Version ?? string.Empty);
    public string UpdatePublishedText => string.Format(L("Help_UpdatePublished"), FormatPublishedDate(DisplayedRelease?.PublishedAt));
    public string UpdateReleaseNotes => string.IsNullOrWhiteSpace(DisplayedRelease?.ReleaseNotes)
        ? L("Help_UpdateNotesEmpty")
        : DisplayedRelease!.ReleaseNotes!;
    public bool CanOpenDownload =>
        ShowUpdateDetails && GitHubUpdateService.IsAllowedReleaseUri(DisplayedRelease?.ReleasePageUri);

    public void Refresh()
    {
        Metadata = _metadataService.GetSnapshot();
        ApplyUpdateState(_updateState.Current);
    }

    partial void OnMetadataChanged(AppMetadata value) => RaiseLocalizedProperties();
    partial void OnCopySucceededChanged(bool value) => OnPropertyChanged(nameof(CopyFeedbackText));
    partial void OnIsCheckingUpdatesChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckUpdatesText));
        RaiseUpdateProperties();
        CheckUpdatesCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsCopyFeedbackVisibleChanged(bool value) => CopySoftwareInfoCommand.NotifyCanExecuteChanged();
    partial void OnUpdateResultChanged(UpdateCheckResult? value) => RaiseUpdateProperties();
    partial void OnLastSuccessfulUpdateResultChanged(UpdateCheckResult? value) => RaiseUpdateProperties();

    private void NavigateToSettings(string? section)
    {
        var target = string.Equals(section, nameof(HelpSettingsSection.Interpreters), StringComparison.Ordinal)
            ? HelpSettingsSection.Interpreters
            : HelpSettingsSection.ScriptPaths;
        SettingsNavigationRequested?.Invoke(this, target);
    }

    private void ShowLocalHelp(string? topic)
    {
        var isFaq = string.Equals(topic, "Faq", StringComparison.Ordinal);
        LocalHelpTitle = L(isFaq ? "Help_Faq" : "Help_RunGuide");
        LocalHelpBody = L(isFaq ? "Help_FaqBody" : "Help_RunGuideBody");
        IsLocalHelpOpen = true;
    }

    private async Task CopySoftwareInfoAsync()
    {
        try
        {
            _clipboard.CopyText(BuildPrivacySafeSoftwareInfo());
            CopySucceeded = true;
        }
        catch (Exception ex)
        {
            _ = ex;
            CopySucceeded = false;
#if DEBUG
            DebugLog.WriteLine($"Help: copy software info failed ({ex.Message})");
#endif
        }

        IsCopyFeedbackVisible = true;
        await Task.Delay(1800);
        IsCopyFeedbackVisible = false;
    }

    private string BuildPrivacySafeSoftwareInfo()
    {
        var lines = new List<string>
        {
            $"PyRunner {DisplayedProductVersion} (Build {Metadata.BuildNumber}, {Metadata.Architecture})",
            $"OS: {Metadata.OperatingSystem} ({Metadata.Architecture})",
            $"Runtime: {Metadata.DotNetRuntime}",
            $"Windows App SDK: {Metadata.WindowsAppSdk.Replace("Windows App SDK ", string.Empty, StringComparison.Ordinal)}",
            $"WebView2: {Metadata.WebView2Runtime}",
            $"Terminal: {Metadata.TerminalComponents}",
        };
        if (!string.IsNullOrWhiteSpace(Metadata.UpdateChannel))
            lines.Add($"Update channel: {Metadata.UpdateChannel}");
        return string.Join(Environment.NewLine, lines);
    }

    private async Task CheckUpdatesAsync()
    {
        if (!CanCheckUpdates || IsCheckingUpdates) return;
        OperationMessage = string.Empty;
        await _updateCoordinator.CheckAsync(UpdateCheckMode.Manual);
    }

    private async Task DownloadUpdateAsync()
    {
        var uri = DisplayedRelease?.ReleasePageUri;
        if (uri is null || !GitHubUpdateService.IsAllowedReleaseUri(uri))
        {
            OperationMessage = L("Help_LinkOpenFailed");
            return;
        }

        OperationMessage = await _externalLinks.OpenAsync(uri) ? string.Empty : L("Help_LinkOpenFailed");
    }

    private string FormatUpdateResult(UpdateCheckResult? result) => result?.Status switch
    {
        UpdateCheckStatus.Latest => L("Help_UpdateLatest"),
        UpdateCheckStatus.UpdateAvailable => string.Format(L("Help_UpdateAvailable"), result.Version ?? string.Empty),
        UpdateCheckStatus.NoPublishedRelease => L("Help_UpdateNoPublishedRelease"),
        UpdateCheckStatus.RateLimited when result.RateLimitResetAt is not null =>
            string.Format(L("Help_UpdateRateLimitedUntil"), FormatPublishedDate(result.RateLimitResetAt)),
        UpdateCheckStatus.RateLimited => L("Help_UpdateRateLimited"),
        UpdateCheckStatus.Offline => L("Help_UpdateOffline"),
        UpdateCheckStatus.Timeout => L("Help_UpdateTimeout"),
        UpdateCheckStatus.InvalidReleaseData => L("Help_UpdateInvalidReleaseData"),
        UpdateCheckStatus.Failed => L("Help_UpdateFailed"),
        _ => string.Empty,
    };

    private async Task OpenExternalLinkAsync(string? linkName)
    {
        var uri = linkName switch
        {
            "Project" => _links.ProjectHomepage,
            "Issues" => _links.IssueTracker,
            "Releases" => _links.ReleasesPage,
            "Author" => _links.AuthorHomepage,
            "License" => _links.LicensePage,
            _ => null,
        };
        if (uri is null) return;
        OperationMessage = await _externalLinks.OpenAsync(uri) ? string.Empty : L("Help_LinkOpenFailed");
    }

    private void OnUpdateStateChanged(object? sender, UpdateStateSnapshot snapshot)
    {
        if (_disposed) return;
        ApplyUpdateState(snapshot);
    }

    private void ApplyUpdateState(UpdateStateSnapshot snapshot)
    {
        IsCheckingUpdates = snapshot.IsChecking;
        UpdateResult = snapshot.LastAttemptResult;
        LastSuccessfulUpdateResult = snapshot.LastSuccessfulResult;
    }

    private string FormatPublishedDate(DateTimeOffset? value)
    {
        if (value is null) return string.Empty;
        var culture = CultureInfo.GetCultureInfo(_localization.CurrentLanguage);
        return value.Value.ToLocalTime().ToString(
            _localization.CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "yyyy年M月d日 HH:mm"
                : "MMM d, yyyy HH:mm",
            culture);
    }

    private string LocalizeChannel(string channel) =>
        string.Equals(channel, "stable", StringComparison.OrdinalIgnoreCase) ? L("Help_ChannelStable") : channel;

    private string L(string key) => _localization[key];

    private void OnLanguageChanged()
    {
        RaiseLocalizedProperties();
        RaiseUpdateProperties();
    }

    private void RaiseUpdateProperties()
    {
        foreach (var propertyName in new[]
        {
            nameof(ShowUpdateStatus), nameof(UpdateStatusText), nameof(DisplayedRelease),
            nameof(ShowUpdateDetails), nameof(UpdateVersionText), nameof(UpdatePublishedText),
            nameof(UpdateReleaseNotes), nameof(CanOpenDownload), nameof(DownloadUpdateText),
            nameof(ReleaseNotesTitle),
        })
            OnPropertyChanged(propertyName);
        DownloadUpdateCommand.NotifyCanExecuteChanged();
    }

    private void RaiseLocalizedProperties()
    {
        foreach (var propertyName in new[]
        {
            nameof(PageTitle), nameof(PageSubtitle), nameof(ProductDescription), nameof(VersionLine),
            nameof(AuthorTitle), nameof(AuthorIdentity), nameof(AuthorAccount), nameof(AuthorBio),
            nameof(SoftwareTitle), nameof(RuntimeInfo), nameof(PythonInfo), nameof(OsInfo),
            nameof(TerminalInfo), nameof(LicenseInfo), nameof(UpdateChannelInfo), nameof(QuickHelpTitle),
            nameof(AddFolderTitle), nameof(AddFolderDescription), nameof(InterpreterTitle),
            nameof(InterpreterDescription), nameof(RunGuideTitle), nameof(RunGuideDescription),
            nameof(FaqTitle), nameof(FaqDescription), nameof(CopyInfoText), nameof(CopyFeedbackText),
            nameof(OnlineResourcesUnavailable), nameof(CopyrightText), nameof(CheckUpdatesText),
            nameof(ProjectHomepageText), nameof(IssueTrackerText), nameof(ReleasesPageText),
            nameof(AuthorHomepageText), nameof(LicensePageText),
        })
            OnPropertyChanged(propertyName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _localization.LanguageChanged -= OnLanguageChanged;
        _updateState.Changed -= OnUpdateStateChanged;
    }
}
