namespace PyRunner.Models;

public enum UpdateDownloadStatus
{
    None,
    Resolving,
    Downloading,
    Verifying,
    Ready,
    Cancelled,
    Failed,
}

public sealed record UpdateReleaseAsset(
    long AssetId,
    string Version,
    string FileName,
    long Size,
    Uri DownloadUri,
    string Sha256,
    DateTimeOffset UpdatedAt,
    Uri ReleasePageUri);

public sealed record DownloadedUpdatePackage(
    UpdateReleaseAsset Asset,
    string FilePath,
    long Size);

public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100);
}

public enum UpdatePackageError
{
    None,
    InvalidRelease,
    AssetNotFound,
    MissingHash,
    InvalidHashFile,
    UntrustedAddress,
    UnsafePath,
    FileTooLarge,
    SizeMismatch,
    HashMismatch,
    AssetChanged,
    Timeout,
    Network,
    Cancelled,
    FileSystem,
}

public sealed record UpdatePackageResult(
    bool Success,
    DownloadedUpdatePackage? Package = null,
    UpdatePackageError Error = UpdatePackageError.None);

public sealed record UpdateAssetResult(
    bool Success,
    UpdateReleaseAsset? Asset = null,
    UpdatePackageError Error = UpdatePackageError.None);

public enum UpdateInstallStatus
{
    Started,
    ActiveRuns,
    Cancelled,
    AssetChanged,
    SizeMismatch,
    HashMismatch,
    UnsafePath,
    LaunchFailed,
    FileSystem,
}

public sealed record UpdateInstallResult(
    UpdateInstallStatus Status,
    DownloadedUpdatePackage? Package = null)
{
    public bool Started => Status == UpdateInstallStatus.Started;
}
