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
                UpdateFeed: new Uri("https://api.github.com/repos/Ethereal-09/PyRunner/releases/latest")));

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
        await viewModel.DownloadUpdateCommand.ExecuteAsync(null);
        Verify(externalLinks.OpenedUris.Count == 1 &&
               externalLinks.OpenedUris[0].AbsoluteUri.EndsWith("/tag/v1.2.0", StringComparison.Ordinal),
            "download command opens the validated GitHub HTTPS release page only");

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
            "Help_UpdateVersion" => "Version: {0}",
            "Help_UpdatePublished" => "Published: {0}",
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
            "1.0.0", "282", "x64", "stable", "@pyrunner_dev", "independent_developer",
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
}
