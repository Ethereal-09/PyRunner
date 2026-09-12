using PyRunner.Models;

namespace PyRunner.Services;

public interface IUpdateReleaseAssetService
{
    Task<UpdateAssetResult> ResolveAsync(
        string version,
        Uri releasePageUri,
        CancellationToken cancellationToken = default);
}

public interface IUpdatePackageDownloader
{
    Task<UpdatePackageResult> DownloadAsync(
        UpdateReleaseAsset asset,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    bool Delete(DownloadedUpdatePackage? package);
}

public interface IUpdatePackageVerifier
{
    Task<UpdatePackageResult> VerifyAsync(
        DownloadedUpdatePackage package,
        UpdateReleaseAsset currentAsset,
        CancellationToken cancellationToken = default);
}

public interface IUpdateInstallConfirmationService
{
    Task<bool> ConfirmAsync(string version, CancellationToken cancellationToken = default);
}

public interface IInstallerLauncher
{
    bool Launch(DownloadedUpdatePackage package);
}

public interface IApplicationExitService
{
    void Exit();
}

public interface IUpdateInstallGuard
{
    bool HasActiveRuns { get; }
    IUpdateInstallLease? TryAcquire();
}

public interface IUpdateInstallLease : IDisposable { }

public interface IVerifiedUpdateInstaller
{
    Task<UpdateInstallResult> VerifyAndLaunchAsync(
        DownloadedUpdatePackage package,
        UpdateReleaseAsset currentAsset,
        IUpdateLaunchAuthorization authorization,
        CancellationToken cancellationToken = default);
}

public interface IUpdateLaunchAuthorization
{
    bool TryLaunch(Func<bool> launch, CancellationToken cancellationToken, out bool launched);
    void Cancel();
}
