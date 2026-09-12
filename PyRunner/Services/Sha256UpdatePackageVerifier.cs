using System.Security.Cryptography;
using PyRunner.Models;

namespace PyRunner.Services;

public sealed class Sha256UpdatePackageVerifier : IUpdatePackageVerifier
{
    public async Task<UpdatePackageResult> VerifyAsync(
        DownloadedUpdatePackage package,
        UpdateReleaseAsset currentAsset,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (package.Asset != currentAsset)
                return new(false, Error: UpdatePackageError.AssetChanged);
            var file = new FileInfo(package.FilePath);
            if (!file.Exists || file.Length != package.Asset.Size || file.Length != package.Size)
                return new(false, Error: UpdatePackageError.SizeMismatch);

            string actual;
            await using (var stream = new FileStream(
                             file.FullName,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            }
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actual),
                    Convert.FromHexString(package.Asset.Sha256)))
                return new(false, Error: UpdatePackageError.HashMismatch);
            cancellationToken.ThrowIfCancellationRequested();
            return new(true, package);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, Error: UpdatePackageError.Cancelled);
        }
        catch (FormatException)
        {
            return new(false, Error: UpdatePackageError.MissingHash);
        }
        catch (Exception)
        {
            return new(false, Error: UpdatePackageError.FileSystem);
        }
    }
}
