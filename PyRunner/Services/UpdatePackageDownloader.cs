using PyRunner.Models;

namespace PyRunner.Services;

public sealed class UpdatePackageDownloader : IUpdatePackageDownloader
{
    public const long MaximumInstallerBytes = 256L * 1024 * 1024;
    private readonly TrustedUpdateHttpClient _http;
    private readonly string _cacheRoot;

    public UpdatePackageDownloader(TrustedUpdateHttpClient http, string? cacheRoot = null)
    {
        _http = http;
        _cacheRoot = Path.GetFullPath(cacheRoot ?? UpdateCachePaths.Root);
    }

    public async Task<UpdatePackageResult> DownloadAsync(
        UpdateReleaseAsset asset,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? attemptDirectory = null;
        string? expectedFileName = null;
        try
        {
            if (asset.Size <= 0 || asset.Size > MaximumInstallerBytes ||
                !GitHubPagesUpdateManifestService.IsExpectedAssetUri(asset.DownloadUri, asset.Version, asset.FileName))
                return new(false, Error: asset.Size > MaximumInstallerBytes
                    ? UpdatePackageError.FileTooLarge
                    : UpdatePackageError.UntrustedAddress);

            Directory.CreateDirectory(_cacheRoot);
            attemptDirectory = Path.Combine(_cacheRoot, $"{asset.Version}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(attemptDirectory);
            expectedFileName = asset.FileName;
            if (!UpdateCachePathGuard.TryOpen(_cacheRoot, attemptDirectory, out var pathGuard) || pathGuard is null)
            {
                TryDeleteAttempt(attemptDirectory, asset.FileName);
                return new(false, Error: UpdatePackageError.UnsafePath);
            }
            using (pathGuard)
            {
                var temporaryPath = Path.Combine(attemptDirectory, asset.FileName + ".partial");
                if (!UpdateCachePathGuard.IsSafeFilePath(_cacheRoot, temporaryPath, asset.FileName + ".partial"))
                    return new(false, Error: CleanupError(
                        attemptDirectory, asset.FileName, UpdatePackageError.UnsafePath));
                var bytes = await _http.DownloadAsync(
                    asset.DownloadUri,
                    temporaryPath,
                    asset.Size,
                    MaximumInstallerBytes,
                    progress,
                    cancellationToken);
                return new(true, new DownloadedUpdatePackage(asset, temporaryPath, bytes));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, Error: CleanupError(attemptDirectory, expectedFileName, UpdatePackageError.Cancelled));
        }
        catch (OperationCanceledException)
        {
            return new(false, Error: CleanupError(attemptDirectory, expectedFileName, UpdatePackageError.Timeout));
        }
        catch (UpdateTransportException ex)
        {
            return new(false, Error: CleanupError(attemptDirectory, expectedFileName, ex.Error));
        }
        catch (HttpRequestException)
        {
            return new(false, Error: CleanupError(attemptDirectory, expectedFileName, UpdatePackageError.Network));
        }
        catch (Exception)
        {
            TryDeleteAttempt(attemptDirectory, expectedFileName);
            return new(false, Error: UpdatePackageError.FileSystem);
        }
    }

    public bool Delete(DownloadedUpdatePackage? package)
    {
        if (package is null) return true;
        try
        {
            var fullPath = Path.GetFullPath(package.FilePath);
            var directory = Path.GetDirectoryName(fullPath);
            return directory is not null && IsUnderCacheRoot(fullPath) &&
                   TryDeleteAttempt(directory, package.Asset.FileName);
        }
        catch { return false; }
    }

    private bool IsUnderCacheRoot(string path) => path.StartsWith(
        _cacheRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase);

    private UpdatePackageError CleanupError(
        string? directory,
        string? assetFileName,
        UpdatePackageError original) =>
        TryDeleteAttempt(directory, assetFileName) ? original : UpdatePackageError.FileSystem;

    private bool TryDeleteAttempt(string? directory, string? assetFileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(assetFileName)) return false;
        try
        {
            var fullDirectory = Path.GetFullPath(directory);
            if (!Directory.Exists(fullDirectory)) return true;
            if (!UpdateCachePathGuard.TryOpen(_cacheRoot, fullDirectory, out var guard) || guard is null)
                return false;
            using (guard)
            {
                foreach (var name in new[] { assetFileName + ".partial", assetFileName })
                {
                    var path = Path.Combine(fullDirectory, name);
                    if (!UpdateCachePathGuard.IsSafeFilePath(_cacheRoot, path, name)) return false;
                    if (File.Exists(path)) File.Delete(path);
                }
            }
            Directory.Delete(fullDirectory, recursive: false);
            return true;
        }
        catch { return false; }
    }
}

public static class UpdateCachePaths
{
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PyRunner",
        "Updates");
}
