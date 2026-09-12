using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PyRunner.Helpers;
using PyRunner.Models;

namespace PyRunner.Services;

public sealed class WinUiUpdateInstallConfirmationService : IUpdateInstallConfirmationService
{
    private readonly ILocalizationService _localization;
    public WinUiUpdateInstallConfirmationService(ILocalizationService localization) => _localization = localization;

    public async Task<bool> ConfirmAsync(string version, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Application.Current is not App app || app.CurrentXamlRoot is not { } xamlRoot)
                return false;
            var dialog = new ContentDialog
            {
                Title = _localization["Help_UpdateInstallConfirmTitle"],
                Content = string.Format(_localization["Help_UpdateInstallConfirmBody"], version),
                PrimaryButtonText = _localization["Help_InstallUpdate"],
                CloseButtonText = _localization["Button_Cancel"],
                DefaultButton = ContentDialogButton.Close,
            };
            DialogHostHelper.Prepare(dialog, xamlRoot);
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            using var registration = cancellationToken.Register(() =>
                dispatcher.TryEnqueue(() =>
                {
                    try { dialog.Hide(); }
                    catch { }
                }));
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class InstallerLauncher : IInstallerLauncher
{
    public bool Launch(DownloadedUpdatePackage package)
    {
        try
        {
            var path = Path.GetFullPath(package.FilePath);
            var root = Path.GetFullPath(UpdateCachePaths.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(path), package.Asset.FileName, StringComparison.Ordinal) ||
                !File.Exists(path))
                return false;
            return Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            }) is not null;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ApplicationExitService : IApplicationExitService
{
    public void Exit() => Application.Current.Exit();
}

public sealed class UpdateInstallGuard : IUpdateInstallGuard
{
    private readonly RunCoordinator _runs;
    public UpdateInstallGuard(RunCoordinator runs) => _runs = runs;
    public bool HasActiveRuns => _runs.HasActiveRuns;
    public IUpdateInstallLease? TryAcquire() => _runs.TryAcquireUpdateInstallLease();
}
