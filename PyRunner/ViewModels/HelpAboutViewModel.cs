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
    private readonly IUpdateReleaseAssetService _releaseAssets;
    private readonly IUpdatePackageDownloader _packageDownloader;
    private readonly IUpdatePackageVerifier _packageVerifier;
    private readonly IUpdateInstallConfirmationService _installConfirmation;
    private readonly IVerifiedUpdateInstaller _verifiedInstaller;
    private readonly IApplicationExitService _applicationExit;
    private readonly IUpdateInstallGuard _installGuard;
    private readonly IUpdateLaunchAuthorization _launchAuthorization = new UpdateLaunchAuthorization();
    private CancellationTokenSource? _downloadCancellation;
    private CancellationTokenSource? _installCancellation;
    private DownloadedUpdatePackage? _verifiedPackage;
    private int _downloadActive;
    private int _installActive;
    private long _lifecycleGeneration;
    private bool _installationLaunched;
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
    [ObservableProperty] private UpdateDownloadStatus updateDownloadStatus;
    [ObservableProperty] private double updateDownloadProgress;

    public IRelayCommand<string> NavigateToSettingsCommand { get; }
    public IRelayCommand<string> ShowLocalHelpCommand { get; }
    public IRelayCommand CloseLocalHelpCommand { get; }
    public IAsyncRelayCommand CopySoftwareInfoCommand { get; }
    public IAsyncRelayCommand CheckUpdatesCommand { get; }
    public IAsyncRelayCommand DownloadUpdateCommand { get; }
    public IRelayCommand CancelUpdateDownloadCommand { get; }
    public IAsyncRelayCommand InstallUpdateCommand { get; }
    public IAsyncRelayCommand OpenReleasePageCommand { get; }
    public IAsyncRelayCommand<string> OpenExternalLinkCommand { get; }

    public event EventHandler<HelpSettingsSection>? SettingsNavigationRequested;

    public HelpAboutViewModel(
        ILocalizationService localization,
        IAppMetadataService metadataService,
        IClipboardService clipboard,
        IExternalLinkService externalLinks,
        UpdateCheckCoordinator updateCoordinator,
        IUpdateStateStore updateState,
        ProductLinksOptions links,
        IUpdateReleaseAssetService releaseAssets,
        IUpdatePackageDownloader packageDownloader,
        IUpdatePackageVerifier packageVerifier,
        IUpdateInstallConfirmationService installConfirmation,
        IVerifiedUpdateInstaller verifiedInstaller,
        IApplicationExitService applicationExit,
        IUpdateInstallGuard installGuard)
    {
        _localization = localization;
        _metadataService = metadataService;
        _clipboard = clipboard;
        _externalLinks = externalLinks;
        _updateCoordinator = updateCoordinator;
        _updateState = updateState;
        _links = links;
        _releaseAssets = releaseAssets;
        _packageDownloader = packageDownloader;
        _packageVerifier = packageVerifier;
        _installConfirmation = installConfirmation;
        _verifiedInstaller = verifiedInstaller;
        _applicationExit = applicationExit;
        _installGuard = installGuard;
        metadata = _metadataService.GetSnapshot();

        NavigateToSettingsCommand = new RelayCommand<string>(NavigateToSettings);
        ShowLocalHelpCommand = new RelayCommand<string>(ShowLocalHelp);
        CloseLocalHelpCommand = new RelayCommand(() => IsLocalHelpOpen = false);
        CopySoftwareInfoCommand = new AsyncRelayCommand(CopySoftwareInfoAsync, () => !IsCopyFeedbackVisible);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, () => !IsCheckingUpdates);
        DownloadUpdateCommand = new AsyncRelayCommand(DownloadUpdateAsync, () => CanDownloadUpdate);
        CancelUpdateDownloadCommand = new RelayCommand(CancelUpdateDownload, () => IsDownloadingUpdate);
        InstallUpdateCommand = new AsyncRelayCommand(InstallUpdateAsync, () => CanInstallUpdate);
        OpenReleasePageCommand = new AsyncRelayCommand(OpenReleasePageAsync, () => CanOpenDownload);
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
    public string DownloadUpdateText => L("Help_DownloadUpdate");
    public string CancelUpdateDownloadText => L("Help_CancelDownload");
    public string InstallUpdateText => L("Help_InstallUpdate");
    public string OpenReleasePageText => L("Help_GoToDownload");
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
    public string UpdateStatusText
    {
        get
        {
            if (IsCheckingUpdates) return L("Help_CheckingUpdates");
            var current = FormatUpdateResult(UpdateResult);
            if (UpdateResult?.IsSuccessful != false || LastSuccessfulUpdateResult?.CheckedAtUtc is not { } checkedAt)
                return current;
            return string.Format(L("Help_UpdateLastSuccessful"), current, FormatPublishedDate(checkedAt));
        }
    }
    public UpdateCheckResult? DisplayedRelease =>
        UpdateResult?.IsSuccessful == true ? UpdateResult : LastSuccessfulUpdateResult;
    public bool ShowUpdateDetails => DisplayedRelease?.Status == UpdateCheckStatus.UpdateAvailable;
    public string UpdateVersionText => string.Format(L("Help_UpdateVersion"), DisplayedRelease?.Version ?? string.Empty);
    public string UpdatePublishedText => string.Format(L("Help_UpdatePublished"), FormatPublishedDate(DisplayedRelease?.PublishedAt));
    public string UpdateReleaseNotes => string.IsNullOrWhiteSpace(DisplayedRelease?.ReleaseNotes)
        ? L("Help_UpdateNotesEmpty")
        : DisplayedRelease!.ReleaseNotes!;
    public bool CanOpenDownload =>
        ShowUpdateDetails && GitHubPagesUpdateManifestService.IsAllowedReleaseUri(DisplayedRelease?.ReleasePageUri);
    public bool IsDownloadingUpdate => UpdateDownloadStatus is
        UpdateDownloadStatus.Resolving or UpdateDownloadStatus.Downloading or UpdateDownloadStatus.Verifying;
    public bool CanDownloadUpdate => CanOpenDownload && !IsDownloadingUpdate &&
        UpdateDownloadStatus != UpdateDownloadStatus.Ready;
    public bool CanInstallUpdate => UpdateDownloadStatus == UpdateDownloadStatus.Ready &&
        _verifiedPackage is { } package && CanOpenDownload &&
        string.Equals(DisplayedRelease?.Version, package.Asset.Version, StringComparison.Ordinal) &&
        DisplayedRelease?.ReleasePageUri == package.Asset.ReleasePageUri;
    public bool ShowCancelUpdateDownload => IsDownloadingUpdate;
    public bool ShowInstallUpdate => CanInstallUpdate;
    public bool ShowDownloadProgress => IsDownloadingUpdate;
    public string UpdateDownloadStatusText => UpdateDownloadStatus switch
    {
        UpdateDownloadStatus.Resolving => L("Help_UpdateResolving"),
        UpdateDownloadStatus.Downloading => string.Format(L("Help_UpdateDownloading"), UpdateDownloadProgress),
        UpdateDownloadStatus.Verifying => L("Help_UpdateVerifying"),
        UpdateDownloadStatus.Ready => L("Help_UpdateReady"),
        UpdateDownloadStatus.Cancelled => L("Help_UpdateDownloadCancelled"),
        UpdateDownloadStatus.Failed => OperationMessage,
        _ => string.Empty,
    };

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
    partial void OnUpdateResultChanged(UpdateCheckResult? value)
    {
        InvalidatePackageIfReleaseChanged(value);
        RaiseUpdateProperties();
    }
    partial void OnLastSuccessfulUpdateResultChanged(UpdateCheckResult? value) => RaiseUpdateProperties();
    partial void OnUpdateDownloadStatusChanged(UpdateDownloadStatus value) => RaiseDownloadProperties();
    partial void OnUpdateDownloadProgressChanged(double value) => OnPropertyChanged(nameof(UpdateDownloadStatusText));
    partial void OnOperationMessageChanged(string value) => OnPropertyChanged(nameof(UpdateDownloadStatusText));

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
        if (Interlocked.CompareExchange(ref _downloadActive, 1, 0) != 0) return;
        var release = DisplayedRelease;
        if (release is not { Status: UpdateCheckStatus.UpdateAvailable, Version: { } version, ReleasePageUri: { } uri } ||
            !GitHubPagesUpdateManifestService.IsAllowedReleaseUri(uri))
        {
            OperationMessage = L("Help_UpdateInvalidReleaseData");
            UpdateDownloadStatus = UpdateDownloadStatus.Failed;
            Interlocked.Exchange(ref _downloadActive, 0);
            return;
        }

        if (_verifiedPackage is not null && !_packageDownloader.Delete(_verifiedPackage))
        {
            SetDownloadFailure(UpdatePackageError.FileSystem);
            Interlocked.Exchange(ref _downloadActive, 0);
            return;
        }
        _verifiedPackage = null;
        _downloadCancellation?.Dispose();
        _downloadCancellation = new CancellationTokenSource();
        var cancellationToken = _downloadCancellation.Token;
        OperationMessage = string.Empty;
        UpdateDownloadProgress = 0;

        try
        {
            UpdateDownloadStatus = UpdateDownloadStatus.Resolving;
            var resolved = await _releaseAssets.ResolveAsync(version, uri, cancellationToken);
            if (!resolved.Success || resolved.Asset is null)
            {
                SetDownloadFailure(resolved.Error);
                return;
            }

            UpdateDownloadStatus = UpdateDownloadStatus.Downloading;
            var progress = new Progress<UpdateDownloadProgress>(value => UpdateDownloadProgress = value.Percentage);
            var downloaded = await _packageDownloader.DownloadAsync(resolved.Asset, progress, cancellationToken);
            if (!downloaded.Success || downloaded.Package is null)
            {
                SetDownloadFailure(downloaded.Error);
                return;
            }

            UpdateDownloadStatus = UpdateDownloadStatus.Verifying;
            var refreshed = await _releaseAssets.ResolveAsync(version, uri, cancellationToken);
            if (!refreshed.Success || refreshed.Asset is null)
            {
                SetDownloadFailure(_packageDownloader.Delete(downloaded.Package)
                    ? refreshed.Error
                    : UpdatePackageError.FileSystem);
                return;
            }
            var verified = await _packageVerifier.VerifyAsync(downloaded.Package, refreshed.Asset, cancellationToken);
            if (!verified.Success || verified.Package is null)
            {
                SetDownloadFailure(_packageDownloader.Delete(downloaded.Package)
                    ? verified.Error
                    : UpdatePackageError.FileSystem);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();

            if (_disposed || !IsCurrentDisplayedRelease(version, uri))
            {
                _packageDownloader.Delete(verified.Package);
                if (!_disposed) SetDownloadFailure(UpdatePackageError.AssetChanged);
                return;
            }

            _verifiedPackage = verified.Package;
            UpdateDownloadProgress = 100;
            UpdateDownloadStatus = UpdateDownloadStatus.Ready;
            OperationMessage = string.Empty;
        }
        finally
        {
            Interlocked.Exchange(ref _downloadActive, 0);
            RaiseDownloadProperties();
        }
    }

    private void CancelUpdateDownload() => _downloadCancellation?.Cancel();

    private async Task OpenReleasePageAsync()
    {
        var uri = DisplayedRelease?.ReleasePageUri;
        OperationMessage = uri is not null && GitHubPagesUpdateManifestService.IsAllowedReleaseUri(uri) &&
                           await _externalLinks.OpenAsync(uri)
            ? string.Empty
            : L("Help_LinkOpenFailed");
    }

    private async Task InstallUpdateAsync()
    {
        if (Interlocked.CompareExchange(ref _installActive, 1, 0) != 0) return;
        var package = _verifiedPackage;
        if (package is null || UpdateDownloadStatus != UpdateDownloadStatus.Ready)
        {
            Interlocked.Exchange(ref _installActive, 0);
            return;
        }
        _installCancellation?.Dispose();
        _installCancellation = new CancellationTokenSource();
        var cancellationToken = _installCancellation.Token;
        var generation = Volatile.Read(ref _lifecycleGeneration);

        try
        {
            if (_installGuard.HasActiveRuns)
            {
                OperationMessage = L("Help_UpdateActiveRuns");
                return;
            }
            if (!await _installConfirmation.ConfirmAsync(package.Asset.Version, cancellationToken)) return;
            if (!IsOperationCurrent(generation, cancellationToken)) return;

            UpdateDownloadStatus = UpdateDownloadStatus.Verifying;
            var refreshed = await _releaseAssets.ResolveAsync(
                package.Asset.Version, package.Asset.ReleasePageUri, cancellationToken);
            if (!IsOperationCurrent(generation, cancellationToken)) return;
            if (!refreshed.Success || refreshed.Asset is null)
            {
                _packageDownloader.Delete(package);
                _verifiedPackage = null;
                SetDownloadFailure(refreshed.Error);
                return;
            }

            using var installLease = _installGuard.TryAcquire();
            if (installLease is null)
            {
                UpdateDownloadStatus = UpdateDownloadStatus.Ready;
                OperationMessage = L("Help_UpdateActiveRuns");
                return;
            }

            var installed = await _verifiedInstaller.VerifyAndLaunchAsync(
                package, refreshed.Asset, _launchAuthorization, cancellationToken);
            if (!IsOperationCurrent(generation, cancellationToken)) return;
            _verifiedPackage = installed.Package;
            if (installed.Started)
            {
                _installationLaunched = true;
                _applicationExit.Exit();
                return;
            }

            switch (installed.Status)
            {
                case UpdateInstallStatus.LaunchFailed:
                    UpdateDownloadStatus = UpdateDownloadStatus.Ready;
                    OperationMessage = L("Help_UpdateLaunchFailed");
                    break;
                case UpdateInstallStatus.Cancelled:
                    UpdateDownloadStatus = UpdateDownloadStatus.Cancelled;
                    OperationMessage = L("Help_UpdateDownloadCancelled");
                    break;
                case UpdateInstallStatus.AssetChanged:
                    FailInstallAndDelete(UpdatePackageError.AssetChanged);
                    break;
                case UpdateInstallStatus.SizeMismatch:
                    FailInstallAndDelete(UpdatePackageError.SizeMismatch);
                    break;
                case UpdateInstallStatus.HashMismatch:
                    FailInstallAndDelete(UpdatePackageError.HashMismatch);
                    break;
                case UpdateInstallStatus.UnsafePath:
                    FailInstallAndDelete(UpdatePackageError.UnsafePath);
                    break;
                default:
                    FailInstallAndDelete(UpdatePackageError.FileSystem);
                    break;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _installActive, 0);
            if (!_disposed) RaiseDownloadProperties();
        }
    }

    private bool IsOperationCurrent(long generation, CancellationToken cancellationToken) =>
        !_disposed && !cancellationToken.IsCancellationRequested &&
        generation == Volatile.Read(ref _lifecycleGeneration);

    private void FailInstallAndDelete(UpdatePackageError error)
    {
        var deleted = _packageDownloader.Delete(_verifiedPackage);
        _verifiedPackage = null;
        SetDownloadFailure(deleted ? error : UpdatePackageError.FileSystem);
    }

    private void SetDownloadFailure(UpdatePackageError error)
    {
        UpdateDownloadStatus = error == UpdatePackageError.Cancelled
            ? UpdateDownloadStatus.Cancelled
            : UpdateDownloadStatus.Failed;
        OperationMessage = L(error switch
        {
            UpdatePackageError.MissingHash => "Help_UpdateMissingHash",
            UpdatePackageError.InvalidHashFile => "Help_UpdateInvalidHashFile",
            UpdatePackageError.HashMismatch => "Help_UpdateHashMismatch",
            UpdatePackageError.SizeMismatch => "Help_UpdateSizeMismatch",
            UpdatePackageError.AssetChanged => "Help_UpdateAssetChanged",
            UpdatePackageError.UntrustedAddress => "Help_UpdateUntrustedAddress",
            UpdatePackageError.UnsafePath => "Help_UpdateUnsafePath",
            UpdatePackageError.FileTooLarge => "Help_UpdateFileTooLarge",
            UpdatePackageError.Cancelled => "Help_UpdateDownloadCancelled",
            UpdatePackageError.Timeout => "Help_UpdateDownloadTimeout",
            UpdatePackageError.AssetNotFound or UpdatePackageError.InvalidRelease => "Help_UpdateInvalidAsset",
            _ => "Help_UpdateDownloadFailed",
        });
    }

    private void InvalidatePackageIfReleaseChanged(UpdateCheckResult? result)
    {
        if (_verifiedPackage is not { } package || result is null) return;
        if (result.Status == UpdateCheckStatus.UpdateAvailable &&
            string.Equals(result.Version, package.Asset.Version, StringComparison.Ordinal) &&
            result.ReleasePageUri == package.Asset.ReleasePageUri)
            return;
        _packageDownloader.Delete(package);
        _verifiedPackage = null;
        UpdateDownloadStatus = UpdateDownloadStatus.None;
        UpdateDownloadProgress = 0;
    }

    private bool IsCurrentDisplayedRelease(string version, Uri releasePageUri) =>
        DisplayedRelease is { Status: UpdateCheckStatus.UpdateAvailable } current &&
        string.Equals(current.Version, version, StringComparison.Ordinal) &&
        current.ReleasePageUri == releasePageUri;

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
        RaiseDownloadProperties();
    }

    private void RaiseUpdateProperties()
    {
        foreach (var propertyName in new[]
        {
            nameof(ShowUpdateStatus), nameof(UpdateStatusText), nameof(DisplayedRelease),
            nameof(ShowUpdateDetails), nameof(UpdateVersionText), nameof(UpdatePublishedText),
            nameof(UpdateReleaseNotes), nameof(CanOpenDownload), nameof(DownloadUpdateText),
            nameof(ReleaseNotesTitle), nameof(CanDownloadUpdate), nameof(CanInstallUpdate),
            nameof(ShowInstallUpdate), nameof(OpenReleasePageText),
        })
            OnPropertyChanged(propertyName);
        DownloadUpdateCommand.NotifyCanExecuteChanged();
        OpenReleasePageCommand.NotifyCanExecuteChanged();
    }

    private void RaiseDownloadProperties()
    {
        foreach (var propertyName in new[]
        {
            nameof(IsDownloadingUpdate), nameof(CanDownloadUpdate), nameof(CanInstallUpdate),
            nameof(ShowCancelUpdateDownload), nameof(ShowInstallUpdate), nameof(ShowDownloadProgress),
            nameof(UpdateDownloadStatusText), nameof(DownloadUpdateText), nameof(CancelUpdateDownloadText),
            nameof(InstallUpdateText), nameof(OpenReleasePageText),
        }) OnPropertyChanged(propertyName);
        DownloadUpdateCommand.NotifyCanExecuteChanged();
        CancelUpdateDownloadCommand.NotifyCanExecuteChanged();
        InstallUpdateCommand.NotifyCanExecuteChanged();
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
        _launchAuthorization.Cancel();
        _disposed = true;
        Interlocked.Increment(ref _lifecycleGeneration);
        _localization.LanguageChanged -= OnLanguageChanged;
        _updateState.Changed -= OnUpdateStateChanged;
        _downloadCancellation?.Cancel();
        _installCancellation?.Cancel();
        _downloadCancellation?.Dispose();
        _installCancellation?.Dispose();
        if (!_installationLaunched) _packageDownloader.Delete(_verifiedPackage);
    }
}
