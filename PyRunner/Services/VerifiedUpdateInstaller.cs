using System.Security.Cryptography;
using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>最终校验期间锁定目录和文件，并在锁仍有效时启动安装包。</summary>
public sealed class VerifiedUpdateInstaller : IVerifiedUpdateInstaller
{
    private readonly IInstallerLauncher _launcher;
    private readonly string _cacheRoot;

    public VerifiedUpdateInstaller(IInstallerLauncher launcher, string? cacheRoot = null)
    {
        _launcher = launcher;
        _cacheRoot = Path.GetFullPath(cacheRoot ?? UpdateCachePaths.Root);
    }

    public async Task<UpdateInstallResult> VerifyAndLaunchAsync(
        DownloadedUpdatePackage package,
        UpdateReleaseAsset currentAsset,
        IUpdateLaunchAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        if (package.Asset != currentAsset)
            return new(UpdateInstallStatus.AssetChanged, package);

        var currentPackage = package;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.GetFullPath(package.FilePath);
            var attempt = Path.GetDirectoryName(source);
            if (attempt is null ||
                !UpdateCachePathGuard.TryOpen(_cacheRoot, attempt, out var pathGuard) || pathGuard is null)
                return new(UpdateInstallStatus.UnsafePath, package);

            using (pathGuard)
            {
                var finalPath = Path.Combine(attempt, package.Asset.FileName);
                var preserveExecutable = false;
                if (source.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
                {
                    if (!UpdateCachePathGuard.IsSafeFilePath(
                            _cacheRoot, source, package.Asset.FileName + ".partial") ||
                        !UpdateCachePathGuard.IsSafeFilePath(
                            _cacheRoot, finalPath, package.Asset.FileName) ||
                        File.Exists(finalPath))
                        return new(UpdateInstallStatus.UnsafePath, package);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(source, finalPath, overwrite: false);
                    currentPackage = package with { FilePath = finalPath };
                }
                else if (!UpdateCachePathGuard.IsSafeFilePath(
                             _cacheRoot, source, package.Asset.FileName))
                {
                    return new(UpdateInstallStatus.UnsafePath, package);
                }

                try
                {
                    if ((File.GetAttributes(currentPackage.FilePath) & FileAttributes.ReparsePoint) != 0)
                        return new(UpdateInstallStatus.UnsafePath, currentPackage);

                    await using var stream = new FileStream(
                        currentPackage.FilePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        64 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (stream.Length != currentAsset.Size || stream.Length != currentPackage.Size)
                        return new(UpdateInstallStatus.SizeMismatch, currentPackage);

                    var actual = await SHA256.HashDataAsync(stream, cancellationToken);
                    var expected = Convert.FromHexString(currentAsset.Sha256);
                    if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                        return new(UpdateInstallStatus.HashMismatch, currentPackage);
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!authorization.TryLaunch(
                            () => _launcher.Launch(currentPackage), cancellationToken, out var launched))
                        return new(UpdateInstallStatus.Cancelled, currentPackage);

                    preserveExecutable = true;
                    return launched
                        ? new(UpdateInstallStatus.Started, currentPackage)
                        : new(UpdateInstallStatus.LaunchFailed, currentPackage);
                }
                finally
                {
                    if (!preserveExecutable && File.Exists(currentPackage.FilePath))
                        File.Delete(currentPackage.FilePath);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(UpdateInstallStatus.Cancelled, currentPackage);
        }
        catch (FormatException)
        {
            return new(UpdateInstallStatus.HashMismatch, currentPackage);
        }
        catch
        {
            return new(UpdateInstallStatus.FileSystem, currentPackage);
        }
    }
}

/// <summary>将页面生命周期取消与不可逆的进程启动线性化。</summary>
public sealed class UpdateLaunchAuthorization : IUpdateLaunchAuthorization
{
    private readonly object _gate = new();
    private bool _cancelled;

    public bool TryLaunch(Func<bool> launch, CancellationToken cancellationToken, out bool launched)
    {
        lock (_gate)
        {
            if (_cancelled || cancellationToken.IsCancellationRequested)
            {
                launched = false;
                return false;
            }

            launched = launch();
            return true;
        }
    }

    public void Cancel()
    {
        lock (_gate) _cancelled = true;
    }
}
