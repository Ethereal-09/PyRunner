using PyRunner.Models;
using PyRunner.Services;
using PyRunner.ViewModels;

internal static class HelpAboutViewModelTests
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Verify(bool condition, string name)
        {
            if (condition)
                Console.WriteLine($"PASS: {name}");
            else
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {name}");
            }
        }

        var localization = new FakeLocalization();
        var metadata = new FakeMetadata();
        var clipboard = new FakeClipboard();
        var externalLinks = new FakeExternalLinks();
        var updates = new FakeUpdateService();
        var cache = new FakeCacheStore();
        var state = new UpdateStateStore();
        var releaseAssets = new FakeReleaseAssetService();
        var packageDownloader = new FakePackageDownloader();
        var packageVerifier = new FakePackageVerifier();
        var confirmation = new FakeConfirmation();
        var launcher = new FakeVerifiedInstaller();
        var applicationExit = new FakeApplicationExit();
        var installGuard = new FakeInstallGuard();
        using var coordinator = new UpdateCheckCoordinator(updates, cache, state, TimeProvider.System);
        using var viewModel = new HelpAboutViewModel(
            localization,
            metadata,
            clipboard,
            externalLinks,
            coordinator,
            state,
            new ProductLinksOptions(
                ReleasesPage: new Uri("https://github.com/Ethereal-09/PyRunner/releases"),
                UpdateFeed: new Uri("https://ethereal-09.github.io/PyRunner/update.json")),
            releaseAssets,
            packageDownloader,
            packageVerifier,
            confirmation,
            launcher,
            applicationExit,
            installGuard);

        HelpSettingsSection? requestedSection = null;
        viewModel.SettingsNavigationRequested += (_, section) => requestedSection = section;
        viewModel.NavigateToSettingsCommand.Execute(nameof(HelpSettingsSection.Interpreters));
        Verify(requestedSection == HelpSettingsSection.Interpreters,
            "help view model requests the selected settings section");

        viewModel.ShowLocalHelpCommand.Execute("Faq");
        Verify(viewModel.IsLocalHelpOpen && viewModel.LocalHelpTitle == "FAQ" && viewModel.LocalHelpBody == "FAQ body",
            "help view model exposes local help state");

        await viewModel.CopySoftwareInfoCommand.ExecuteAsync(null);
        Verify(clipboard.Text.Contains("PyRunner 1.0.0", StringComparison.Ordinal) &&
               clipboard.Text.Contains("WebView2", StringComparison.Ordinal) &&
               !clipboard.Text.Contains("C:\\Users", StringComparison.OrdinalIgnoreCase) &&
               !clipboard.Text.Contains("python.exe", StringComparison.OrdinalIgnoreCase),
            "help copy output contains diagnostics without private paths");

        metadata.Snapshot = metadata.Snapshot with { Version = "1.1.0", BuildNumber = "300" };
        viewModel.Refresh();
        Verify(viewModel.VersionLine.Contains("1.1.0", StringComparison.Ordinal) &&
               viewModel.VersionLine.Contains("300", StringComparison.Ordinal),
            "help view model refreshes runtime metadata on re-entry");

        metadata.Snapshot = metadata.Snapshot with { Version = string.Empty };
        viewModel.Refresh();
        Verify(viewModel.VersionLine.Contains("Unknown version", StringComparison.Ordinal),
            "missing assembly product version is localized as unknown");
        metadata.Snapshot = metadata.Snapshot with { Version = "1.1.0" };
        viewModel.Refresh();

        var firstCheck = viewModel.CheckUpdatesCommand.ExecuteAsync(null);
        var secondCheck = viewModel.CheckUpdatesCommand.ExecuteAsync(null);
        await Task.WhenAll(firstCheck, secondCheck);
        Verify(updates.CallCount == 1 && viewModel.UpdateResult?.Status == UpdateCheckStatus.Latest,
            "help manual command shares the coordinator in-flight request");

        updates.Result = new UpdateCheckResult(
            UpdateCheckStatus.UpdateAvailable,
            "1.2.0",
            DateTimeOffset.Parse("2026-09-01T08:00:00Z"),
            "Plain release notes",
            new Uri("https://github.com/Ethereal-09/PyRunner/releases/tag/v1.2.0"),
            "\"etag-download\"");
        await viewModel.CheckUpdatesCommand.ExecuteAsync(null);
        packageVerifier.Error = UpdatePackageError.HashMismatch;
        var firstDownload = viewModel.DownloadUpdateCommand.ExecuteAsync(null);
        var duplicateDownload = viewModel.DownloadUpdateCommand.ExecuteAsync(null);
        await Task.WhenAll(firstDownload, duplicateDownload);
        Verify(packageDownloader.CallCount == 1 && !viewModel.CanInstallUpdate && launcher.CallCount == 0,
            "duplicate download clicks share one operation and failed verification never enables launch");

        packageVerifier.Error = UpdatePackageError.None;
        await viewModel.DownloadUpdateCommand.ExecuteAsync(null);
        Verify(packageDownloader.CallCount == 2 && viewModel.CanInstallUpdate && launcher.CallCount == 0,
            "successful retry enables install without launching before confirmation");

        localization.SetLanguage("zh-CN");
        Verify(viewModel.InstallUpdateText == "安装更新" && viewModel.UpdateDownloadStatusText == "更新已校验",
            "language changes refresh ready-state update actions and status text");
        localization.SetLanguage("en-US");

        confirmation.Result = false;
        await viewModel.InstallUpdateCommand.ExecuteAsync(null);
        Verify(launcher.CallCount == 0 && applicationExit.CallCount == 0,
            "user refusal never launches installer or exits application");

        confirmation.Result = true;
        launcher.Result = false;
        installGuard.HasActiveRuns = true;
        await viewModel.InstallUpdateCommand.ExecuteAsync(null);
        Verify(launcher.CallCount == 0 && applicationExit.CallCount == 0,
            "running scripts block update installation without terminating them");
        installGuard.HasActiveRuns = false;
        installGuard.DenyAcquire = true;
        await viewModel.InstallUpdateCommand.ExecuteAsync(null);
        Verify(launcher.CallCount == 0 && applicationExit.CallCount == 0,
            "a script starting after confirmation prevents acquiring the install lease");
        installGuard.DenyAcquire = false;
        await viewModel.InstallUpdateCommand.ExecuteAsync(null);
        Verify(launcher.CallCount == 1 && applicationExit.CallCount == 0 && viewModel.CanInstallUpdate,
            "installer launch failure keeps PyRunner open and permits retry");

        launcher.Result = true;
        await viewModel.InstallUpdateCommand.ExecuteAsync(null);
        Verify(launcher.CallCount == 2 && applicationExit.CallCount == 1,
            "application exits only after verified installer launch succeeds");

        await viewModel.OpenReleasePageCommand.ExecuteAsync(null);
        Verify(externalLinks.OpenedUris.Count == 1 &&
               externalLinks.OpenedUris[0].AbsoluteUri.EndsWith("/tag/v1.2.0", StringComparison.Ordinal),
            "validated GitHub Release remains available as fallback");

        state.Complete(new UpdateCheckResult(UpdateCheckStatus.Offline));
        Verify(viewModel.UpdateStatusText.Contains("Last successful check:", StringComparison.Ordinal),
            "failed update check displays the timestamped last successful result");

        state.Complete(updates.Result with
        {
            ReleasePageUri = new Uri("https://example.com/untrusted.exe"),
        });
        Verify(!viewModel.CanOpenDownload && !viewModel.DownloadUpdateCommand.CanExecute(null),
            "download command rejects non-GitHub and non-release URLs");

        localization.PageTitle = "帮助页已刷新";
        localization.RaiseChanged();
        Verify(viewModel.PageTitle == "帮助页已刷新",
            "help view model refreshes localized text without recreating the page");

        var resultBeforeDispose = viewModel.UpdateResult;
        viewModel.Dispose();
        state.Complete(new UpdateCheckResult(UpdateCheckStatus.Failed));
        Verify(ReferenceEquals(viewModel.UpdateResult, resultBeforeDispose),
            "disposed help view model no longer receives update-state notifications");

        var lifecycleState = new UpdateStateStore();
        var lifecycleUpdates = new FakeUpdateService { Result = updates.Result };
        using var lifecycleCoordinator = new UpdateCheckCoordinator(
            lifecycleUpdates, new FakeCacheStore(), lifecycleState, TimeProvider.System);
        var lifecycleInstaller = new FakeVerifiedInstaller { BlockUntilCancelled = true };
        var lifecycleExit = new FakeApplicationExit();
        var lifecycleConfirmation = new FakeConfirmation { Result = true };
        var lifecycleViewModel = new HelpAboutViewModel(
            new FakeLocalization(), new FakeMetadata(), new FakeClipboard(), new FakeExternalLinks(),
            lifecycleCoordinator, lifecycleState,
            new ProductLinksOptions(
                ReleasesPage: new Uri("https://github.com/Ethereal-09/PyRunner/releases"),
                UpdateFeed: new Uri("https://ethereal-09.github.io/PyRunner/update.json")),
            new FakeReleaseAssetService(), new FakePackageDownloader(), new FakePackageVerifier(),
            lifecycleConfirmation, lifecycleInstaller, lifecycleExit, new FakeInstallGuard());
        lifecycleState.Complete(lifecycleUpdates.Result);
        await lifecycleViewModel.DownloadUpdateCommand.ExecuteAsync(null);
        var closingInstall = lifecycleViewModel.InstallUpdateCommand.ExecuteAsync(null);
        await lifecycleInstaller.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        lifecycleViewModel.Dispose();
        await closingInstall;
        Verify(lifecycleInstaller.CallCount == 1 && lifecycleExit.CallCount == 0,
            "closing the page cancels final verification and never exits or launches afterward");

        return failures;

    }

    private sealed class FakeLocalization : ILocalizationService
    {
        public string PageTitle { get; set; } = "Help";
        public string CurrentLanguage { get; private set; } = "en-US";
        public event Action? LanguageChanged;

        public string this[string key] => key switch
        {
            "Help_Title" => PageTitle,
            "Help_VersionLine" => "Version {0} · Build {1} · {2} · {3}",
            "Help_UnknownVersion" => "Unknown version",
            "Help_ChannelStable" => "Stable",
            "Help_RuntimeInfo" => "Runtime: {0} · {1}",
            "Help_PythonInfo" => "Python: {0}",
            "Help_OsInfo" => "OS: {0}",
            "Help_TerminalInfo" => "Terminal: WebView2 {0} · {1}",
            "Help_LicenseInfo" => "License: {0}",
            "Help_UpdateChannelInfo" => "Update: {0}",
            "Help_Copyright" => "© {0} PyRunner",
            "Help_Faq" => "FAQ",
            "Help_FaqBody" => "FAQ body",
            "Help_UpdateLatest" => "Latest",
            "Help_UpdateAvailable" => "Available {0}",
            "Help_UpdateOffline" => "Offline.",
            "Help_UpdateLastSuccessful" => "{0} Last successful check: {1}.",
            "Help_UpdateVersion" => "Version: {0}",
            "Help_UpdatePublished" => "Published: {0}",
            "Help_InstallUpdate" => CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "安装更新" : "Install update",
            "Help_UpdateReady" => CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "更新已校验" : "Update verified",
            _ => key,
        };

        public void SetLanguage(string languageCode)
        {
            CurrentLanguage = languageCode;
            RaiseChanged();
        }

        public void RaiseChanged() => LanguageChanged?.Invoke();
    }

    private sealed class FakeMetadata : IAppMetadataService
    {
        public AppMetadata Snapshot { get; set; } = new(
            "1.0.0", "282", "x64", "stable", "@Ethereal-09", "independent_developer",
            ".NET 8.0.29", "Windows App SDK 1.5", "Windows 11", "3.13.13",
            "152.0", "xterm.js · ConPTY", string.Empty, "stable");

        public AppMetadata GetSnapshot() => Snapshot;
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public string Text { get; private set; } = string.Empty;
        public void CopyText(string text) => Text = text;
    }

    private sealed class FakeExternalLinks : IExternalLinkService
    {
        public List<Uri> OpenedUris { get; } = [];
        public Task<bool> OpenAsync(Uri uri)
        {
            OpenedUris.Add(uri);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public int CallCount { get; private set; }
        public UpdateCheckResult Result { get; set; } = new(
            UpdateCheckStatus.Latest,
            "1.0.0",
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            string.Empty,
            new Uri("https://github.com/Ethereal-09/PyRunner/releases/tag/v1.0.0"));

        public async Task<UpdateCheckResult> CheckLatestAsync(
            UpdateCheckCache? cachedResult,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Delay(40, cancellationToken);
            return Result;
        }

        public UpdateCheckResult? RestoreCachedResult(UpdateCheckCache? cachedResult) => null;
    }

    private sealed class FakeCacheStore : IUpdateCacheStore
    {
        private UpdateCacheSnapshot _snapshot = new(null, null);
        public UpdateCacheSnapshot Load() => _snapshot;
        public void SaveAttempt(DateTimeOffset attemptedAtUtc) =>
            _snapshot = _snapshot with { LastAttemptAtUtc = attemptedAtUtc };
        public void SaveSuccessfulResult(UpdateCheckCache result) =>
            _snapshot = _snapshot with { LastSuccessfulResult = result };
    }

    private sealed class FakeReleaseAssetService : IUpdateReleaseAssetService
    {
        public Task<UpdateAssetResult> ResolveAsync(string version, Uri releasePageUri, CancellationToken cancellationToken = default)
        {
            var asset = new UpdateReleaseAsset(
                42, version, $"PyRunner-Setup-{version}-x64.exe", 4,
                new Uri($"https://github.com/Ethereal-09/PyRunner/releases/download/v{version}/PyRunner-Setup-{version}-x64.exe"),
                new string('A', 64), DateTimeOffset.Parse("2026-09-01T00:00:00Z"), releasePageUri);
            return Task.FromResult(new UpdateAssetResult(true, asset));
        }
    }

    private sealed class FakePackageDownloader : IUpdatePackageDownloader
    {
        public int CallCount { get; private set; }
        public async Task<UpdatePackageResult> DownloadAsync(UpdateReleaseAsset asset, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Delay(40, cancellationToken);
            progress?.Report(new UpdateDownloadProgress(4, 4));
            return new(true, new DownloadedUpdatePackage(asset, "fake.partial", 4));
        }
        public bool Delete(DownloadedUpdatePackage? package) => true;
    }

    private sealed class FakePackageVerifier : IUpdatePackageVerifier
    {
        public UpdatePackageError Error { get; set; }
        public Task<UpdatePackageResult> VerifyAsync(DownloadedUpdatePackage package, UpdateReleaseAsset currentAsset, CancellationToken cancellationToken = default) =>
            Task.FromResult(Error != UpdatePackageError.None
                ? new UpdatePackageResult(false, Error: Error)
                : package.Asset == currentAsset
                ? new UpdatePackageResult(true, package with { FilePath = currentAsset.FileName })
                : new UpdatePackageResult(false, Error: UpdatePackageError.AssetChanged));
    }

    private sealed class FakeConfirmation : IUpdateInstallConfirmationService
    {
        public bool Result { get; set; }
        public Task<bool> ConfirmAsync(string version, CancellationToken cancellationToken = default) => Task.FromResult(Result);
    }

    private sealed class FakeVerifiedInstaller : IVerifiedUpdateInstaller
    {
        public int CallCount { get; private set; }
        public bool Result { get; set; }
        public bool BlockUntilCancelled { get; set; }
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<UpdateInstallResult> VerifyAndLaunchAsync(
            DownloadedUpdatePackage package,
            UpdateReleaseAsset currentAsset,
            IUpdateLaunchAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Entered.TrySetResult(true);
            if (BlockUntilCancelled)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { }
                return new UpdateInstallResult(UpdateInstallStatus.Cancelled, package);
            }
            return new UpdateInstallResult(
                Result ? UpdateInstallStatus.Started : UpdateInstallStatus.LaunchFailed, package);
        }
    }

    private sealed class FakeApplicationExit : IApplicationExitService
    {
        public int CallCount { get; private set; }
        public void Exit() => CallCount++;
    }

    private sealed class FakeInstallGuard : IUpdateInstallGuard
    {
        public bool HasActiveRuns { get; set; }
        public bool DenyAcquire { get; set; }
        public IUpdateInstallLease? TryAcquire() => HasActiveRuns || DenyAcquire ? null : new FakeLease();
        private sealed class FakeLease : IUpdateInstallLease { public void Dispose() { } }
    }
}
