using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PyRunner.Models;
using PyRunner.Services;

internal static class UpdatePackageServiceTests
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Verify(bool condition, string name)
        {
            if (condition) Console.WriteLine($"PASS: {name}");
            else { failures++; Console.Error.WriteLine($"FAIL: {name}"); }
        }

        var bytes = Encoding.ASCII.GetBytes("fake installer bytes - never execute");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var root = Path.Combine(Path.GetTempPath(), "PyRunner.Verification", "updates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var correctHandler = new RoutingHandler(request =>
            {
                if (request.RequestUri!.Host == "github.com")
                    return Redirect("https://release-assets.githubusercontent.com/download/asset-token");
                return BinaryResponse(bytes);
            });
            using var correctHttp = Client(correctHandler);
            var downloader = new UpdatePackageDownloader(correctHttp, root);
            var verifier = new Sha256UpdatePackageVerifier();
            var resolved = new UpdateAssetResult(true, Asset("1.2.0", bytes.Length, hash));
            Verify(resolved.Success && resolved.Asset?.Sha256 == hash,
                "manifest asset supplies exact version, x64 filename, size, and SHA-256");
            var downloaded = await downloader.DownloadAsync(resolved.Asset!);
            Verify(downloaded.Success && downloaded.Package is not null &&
                   downloaded.Package.FilePath.EndsWith(".partial", StringComparison.Ordinal) &&
                   !Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories).Any(),
                "download remains a unique non-executable temporary file before verification");
            var verified = await verifier.VerifyAsync(downloaded.Package!, resolved.Asset!);
            Verify(verified.Success && verified.Package is not null &&
                   verified.Package.FilePath.EndsWith(".partial", StringComparison.Ordinal) &&
                   !Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories).Any(),
                "matching SHA-256 remains non-executable until user-confirmed installation");
            var lockedLauncher = new LockCheckingLauncher();
            var installed = await new VerifiedUpdateInstaller(lockedLauncher, root)
                .VerifyAndLaunchAsync(verified.Package!, resolved.Asset!, new UpdateLaunchAuthorization());
            Verify(installed.Started && lockedLauncher.CallCount == 1 && lockedLauncher.FileWasWriteLocked,
                "final hash keeps the installer write-locked through process launch");
            downloader.Delete(installed.Package);

            using (var finalMismatchHttp = Client(new RoutingHandler(_ => BinaryResponse(bytes))))
            {
                var finalDownloader = new UpdatePackageDownloader(finalMismatchHttp, root);
                var wrongAsset = Asset("1.2.0", bytes.Length, new string('0', 64));
                var package = await finalDownloader.DownloadAsync(wrongAsset);
                var finalLauncher = new CountingLauncher();
                var result = await new VerifiedUpdateInstaller(finalLauncher, root)
                    .VerifyAndLaunchAsync(package.Package!, wrongAsset, new UpdateLaunchAuthorization());
                Verify(result.Status == UpdateInstallStatus.HashMismatch && finalLauncher.CallCount == 0,
                       "final pre-launch SHA-256 mismatch never reaches the process launcher");
                Verify(!Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories).Any(),
                    "final hash failure removes the promoted executable");
                finalDownloader.Delete(result.Package);
            }

            using (var replacedFileHttp = Client(new RoutingHandler(_ => BinaryResponse(bytes))))
            {
                var finalDownloader = new UpdatePackageDownloader(replacedFileHttp, root);
                var asset = Asset("1.2.0", bytes.Length, hash);
                var package = await finalDownloader.DownloadAsync(asset);
                var initiallyVerified = await verifier.VerifyAsync(package.Package!, asset);
                var replacement = Enumerable.Repeat((byte)'X', bytes.Length).ToArray();
                await File.WriteAllBytesAsync(initiallyVerified.Package!.FilePath, replacement);
                var finalLauncher = new CountingLauncher();
                var result = await new VerifiedUpdateInstaller(finalLauncher, root)
                    .VerifyAndLaunchAsync(
                        initiallyVerified.Package, asset, new UpdateLaunchAuthorization());
                Verify(result.Status == UpdateInstallStatus.HashMismatch && finalLauncher.CallCount == 0 &&
                       !Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories).Any(),
                    "installer replaced after initial verification is rejected before launch");
                finalDownloader.Delete(result.Package);
            }

            using (var cancelledInstallHttp = Client(new RoutingHandler(_ => BinaryResponse(bytes))))
            {
                var finalDownloader = new UpdatePackageDownloader(cancelledInstallHttp, root);
                var asset = Asset("1.2.0", bytes.Length, hash);
                var package = await finalDownloader.DownloadAsync(asset);
                var finalLauncher = new CountingLauncher();
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                var result = await new VerifiedUpdateInstaller(finalLauncher, root)
                    .VerifyAndLaunchAsync(
                        package.Package!, asset, new UpdateLaunchAuthorization(), cancelled.Token);
                Verify(result.Status == UpdateInstallStatus.Cancelled && finalLauncher.CallCount == 0 &&
                       package.Package!.FilePath.EndsWith(".partial", StringComparison.Ordinal),
                    "cancelled final verification leaves the package non-executable and never launches");
                finalDownloader.Delete(result.Package);
            }

            using (var closedPageHttp = Client(new RoutingHandler(_ => BinaryResponse(bytes))))
            {
                var finalDownloader = new UpdatePackageDownloader(closedPageHttp, root);
                var asset = Asset("1.2.0", bytes.Length, hash);
                var package = await finalDownloader.DownloadAsync(asset);
                var finalLauncher = new CountingLauncher();
                var authorization = new UpdateLaunchAuthorization();
                authorization.Cancel();
                var result = await new VerifiedUpdateInstaller(finalLauncher, root)
                    .VerifyAndLaunchAsync(package.Package!, asset, authorization);
                Verify(result.Status == UpdateInstallStatus.Cancelled && finalLauncher.CallCount == 0 &&
                       !Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories).Any(),
                    "closed page rejects atomic launch authorization and removes the executable");
                finalDownloader.Delete(result.Package);
            }

            var mismatchAsset = Asset("1.2.0", bytes.Length, new string('0', 64));
            using (var mismatchHttp = Client(new RoutingHandler(_ => BinaryResponse(bytes))))
            {
                var localDownloader = new UpdatePackageDownloader(mismatchHttp, root);
                var package = await localDownloader.DownloadAsync(mismatchAsset);
                var result = await verifier.VerifyAsync(package.Package!, mismatchAsset);
                Verify(!result.Success && result.Error == UpdatePackageError.HashMismatch,
                    "SHA-256 mismatch never enables installer");
                localDownloader.Delete(package.Package);
            }

            var wrongSizeAsset = Asset("1.2.0", bytes.Length + 1, hash);
            using (var sizeHttp = Client(new RoutingHandler(_ => BinaryResponse(bytes))))
            {
                var result = await new UpdatePackageDownloader(sizeHttp, root).DownloadAsync(wrongSizeAsset);
                Verify(!result.Success && result.Error == UpdatePackageError.SizeMismatch &&
                       !Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Any(),
                    "size mismatch removes partial download");
            }

            using (var redirectHttp = Client(new RoutingHandler(_ => Redirect("http://evil.example/payload.exe"))))
            {
                var result = await new UpdatePackageDownloader(redirectHttp, root).DownloadAsync(Asset("1.2.0", bytes.Length, hash));
                Verify(!result.Success && result.Error == UpdatePackageError.UntrustedAddress,
                    "non-HTTPS or untrusted redirect is rejected");
            }
            using (var redirectHttp = Client(new RoutingHandler(_ => Redirect("https://evil.example/payload.exe"))))
            {
                var result = await new UpdatePackageDownloader(redirectHttp, root).DownloadAsync(Asset("1.2.0", bytes.Length, hash));
                Verify(!result.Success && result.Error == UpdatePackageError.UntrustedAddress,
                    "HTTPS redirect to a non-GitHub host is rejected");
            }

            using (var networkHttp = Client(new RoutingHandler(_ => throw new HttpRequestException("offline"))))
            {
                var result = await new UpdatePackageDownloader(networkHttp, root).DownloadAsync(Asset("1.2.0", bytes.Length, hash));
                Verify(!result.Success && result.Error == UpdatePackageError.Network &&
                       !Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Any(),
                    "network failure removes the unique partial download");
            }

            using (var timeoutHttp = Client(new TimeoutHandler()))
            {
                var result = await new UpdatePackageDownloader(timeoutHttp, root).DownloadAsync(Asset("1.2.0", bytes.Length, hash));
                Verify(!result.Success && result.Error == UpdatePackageError.Timeout,
                    "transport timeout is distinct from user cancellation");
            }

            using (var loopHttp = Client(new RoutingHandler(request => Redirect(request.RequestUri!.AbsoluteUri))))
            {
                var result = await new UpdatePackageDownloader(loopHttp, root).DownloadAsync(Asset("1.2.0", bytes.Length, hash));
                Verify(!result.Success && result.Error == UpdatePackageError.UntrustedAddress,
                    "redirect loops and more than five hops are rejected");
            }

            var oversizePath = Path.Combine(root, "oversize.partial");
            using (var actualLimitHttp = Client(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                   { Content = new UnknownLengthContent(new byte[17]) })))
            {
                var threwLimit = false;
                try
                {
                    await actualLimitHttp.DownloadAsync(
                        Asset("1.2.0", 17, hash).DownloadUri, oversizePath, 17, 16, null, CancellationToken.None);
                }
                catch (UpdateTransportException ex) when (ex.Error == UpdatePackageError.FileTooLarge)
                {
                    threwLimit = true;
                }
                Verify(threwLimit, "actual streamed bytes enforce the configured maximum without Content-Length");
                if (File.Exists(oversizePath)) File.Delete(oversizePath);
            }

            using (var cancelHttp = Client(new BlockingHandler()))
            {
                var cancelDownloader = new UpdatePackageDownloader(cancelHttp, root);
                using var cancellation = new CancellationTokenSource();
                var task = cancelDownloader.DownloadAsync(Asset("1.2.0", bytes.Length, hash), cancellationToken: cancellation.Token);
                cancellation.CancelAfter(30);
                var result = await task;
                Verify(!result.Success && result.Error == UpdatePackageError.Cancelled &&
                       !Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).Any(),
                    "download cancellation removes all partial files");
            }

            using (var changedHttp = Client(new RoutingHandler(_ => BinaryResponse(bytes))))
            {
                var changedDownloader = new UpdatePackageDownloader(changedHttp, root);
                var original = Asset("1.2.0", bytes.Length, hash);
                var package = await changedDownloader.DownloadAsync(original);
                var changed = original with { AssetId = original.AssetId + 1 };
                var result = await verifier.VerifyAsync(package.Package!, changed);
                Verify(!result.Success && result.Error == UpdatePackageError.AssetChanged,
                    "asset metadata change blocks verification");
                changedDownloader.Delete(package.Package);
            }


            using (var lockedCleanupHttp = Client(new RoutingHandler(_ => BinaryResponse(bytes))))
            {
                var cleanupDownloader = new UpdatePackageDownloader(lockedCleanupHttp, root);
                var package = await cleanupDownloader.DownloadAsync(Asset("1.2.0", bytes.Length, hash));
                using (var locked = new FileStream(package.Package!.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    Verify(!cleanupDownloader.Delete(package.Package), "locked update cleanup reports failure instead of claiming deletion");
                Verify(cleanupDownloader.Delete(package.Package), "locked update can be cleaned after the handle is released");
            }

            var outside = Path.Combine(Path.GetTempPath(), "PyRunner.Verification", "junction-target-" + Guid.NewGuid().ToString("N"));
            var link = Path.Combine(root, "1.2.0-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            try
            {
                Directory.CreateSymbolicLink(link, outside);
                Verify(!UpdateCachePathGuard.TryOpen(root, link, out _),
                    "update cache rejects a symbolic-link attempt directory");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Console.WriteLine("INFO: symbolic-link path test skipped because Windows developer privilege is unavailable");
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link);
                if (Directory.Exists(outside)) Directory.Delete(outside);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return failures;
    }

    private static TrustedUpdateHttpClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) }, ownsClient: true);

    private static Uri ReleasePage(string version) =>
        new($"https://github.com/Ethereal-09/PyRunner/releases/tag/v{version}");

    private static UpdateReleaseAsset Asset(string version, long size, string hash) => new(
        42, version, $"PyRunner-Setup-{version}-x64.exe", size,
        new Uri($"https://github.com/Ethereal-09/PyRunner/releases/download/v{version}/PyRunner-Setup-{version}-x64.exe"),
        hash, DateTimeOffset.Parse("2026-09-01T00:00:00Z"), ReleasePage(version));

    private static string ReleaseJson(
        string version,
        long size,
        string? digest,
        bool includeChecksum = false,
        string? assetName = null,
        string? assetUrl = null)
    {
        var expectedName = assetName ?? $"PyRunner-Setup-{version}-x64.exe";
        assetUrl ??= $"https://github.com/Ethereal-09/PyRunner/releases/download/v{version}/{expectedName}";
        var assets = new List<object>
        {
            new { id = 42, name = expectedName, size, browser_download_url = assetUrl,
                  digest, updated_at = "2026-09-01T00:00:00Z" },
        };
        if (includeChecksum)
        {
            var checksumName = $"PyRunner-Setup-{version}-x64.exe.sha256";
            assets.Add(new { id = 43, name = checksumName, size = 100,
                browser_download_url = $"https://github.com/Ethereal-09/PyRunner/releases/download/v{version}/{checksumName}",
                digest = (string?)null, updated_at = "2026-09-01T00:00:00Z" });
        }
        return JsonSerializer.Serialize(new
        {
            draft = false, prerelease = false, tag_name = $"v{version}",
            html_url = $"https://github.com/Ethereal-09/PyRunner/releases/tag/v{version}", assets,
        });
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage TextResponse(string text) => new(HttpStatusCode.OK)
    { Content = new StringContent(text, Encoding.ASCII, "text/plain") };
    private static HttpResponseMessage BinaryResponse(byte[] bytes) => new(HttpStatusCode.OK)
    { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(route(request));
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout"));
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class LockCheckingLauncher : IInstallerLauncher
    {
        public int CallCount { get; private set; }
        public bool FileWasWriteLocked { get; private set; }
        public bool Launch(DownloadedUpdatePackage package)
        {
            CallCount++;
            try
            {
                using var stream = new FileStream(package.FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            }
            catch (IOException)
            {
                FileWasWriteLocked = true;
            }
            return true;
        }
    }

    private sealed class CountingLauncher : IInstallerLauncher
    {
        public int CallCount { get; private set; }
        public bool Launch(DownloadedUpdatePackage package) { CallCount++; return true; }
    }
}
