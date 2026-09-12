using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PyRunner.Models;
using PyRunner.Services;

internal static class GitHubUpdateServiceTests
{
    private static readonly Uri ManifestUri = new("https://ethereal-09.github.io/PyRunner/update.json");

    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Verify(bool condition, string name)
        {
            if (condition) Console.WriteLine($"PASS: {name}");
            else { failures++; Console.Error.WriteLine($"FAIL: {name}"); }
        }

        HttpRequestMessage? observed = null;
        var normal = await CreateService(new DelegateHandler((request, _) =>
        {
            observed = request;
            return Task.FromResult(JsonResponse(ManifestJson(), "\"pages-etag\""));
        })).CheckLatestAsync(null);
        Verify(observed?.RequestUri == ManifestUri && observed.RequestUri.Host != "api.github.com",
            "update check requests only the fixed GitHub Pages manifest");
        Verify(observed?.Headers.Accept.Any(value => value.MediaType == "application/json") == true &&
               observed.Headers.UserAgent.ToString() == "PyRunner",
            "Pages manifest request uses JSON accept and product user agent");
        Verify(normal.Status == UpdateCheckStatus.UpdateAvailable && normal.Version == "1.2.3" &&
               normal.ETag == "\"pages-etag\"",
            "valid schema v1 manifest reports a stable update");

        var current = await CreateService(Handler(ManifestJson()), "1.2.3").CheckLatestAsync(null);
        Verify(current.Status == UpdateCheckStatus.Latest, "current manifest version reports latest");

        foreach (var json in InvalidManifests())
        {
            var result = await CreateService(Handler(json)).CheckLatestAsync(null);
            Verify(result.Status == UpdateCheckStatus.InvalidReleaseData,
                "invalid schema, identity, asset, URL, size, or hash is rejected");
        }

        var cached = CachedResult("1.2.2", "\"cached\"");
        HttpRequestMessage? conditional = null;
        var notModified = await CreateService(new DelegateHandler((request, _) =>
        {
            conditional = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        })).CheckLatestAsync(cached);
        Verify(conditional?.Headers.IfNoneMatch.Single().Tag == "\"cached\"" &&
               notModified.FromCache && notModified.Version == "1.2.2",
            "304 restores the validated cached Pages result");

        var retryCalls = 0;
        var retry = await CreateService(new DelegateHandler((_, _) =>
        {
            retryCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        })).CheckLatestAsync(null);
        Verify(retryCalls == 1 && retry.Status == UpdateCheckStatus.InvalidReleaseData,
            "304 without an ETag cache is rejected without a retry loop");

        var missing = await CreateService(Response(HttpStatusCode.NotFound)).CheckLatestAsync(null);
        Verify(missing.Status == UpdateCheckStatus.NoPublishedRelease,
            "Pages 404 reports no published manifest");
        var throttledResponse = new HttpResponseMessage((HttpStatusCode)429);
        throttledResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
        var throttled = await CreateService(new DelegateHandler((_, _) => Task.FromResult(throttledResponse)))
            .CheckLatestAsync(null);
        Verify(throttled.Status == UpdateCheckStatus.RateLimited && throttled.RateLimitResetAt is not null,
            "Pages 429 is cooled down without retry or GitHub API fallback");

        var offline = await CreateService(new DelegateHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "offline")))
            .CheckLatestAsync(null);
        Verify(offline.Status == UpdateCheckStatus.Offline,
            "Pages name-resolution failure reports offline");
        var timeout = await CreateService(new DelegateHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse(ManifestJson());
        }), timeout: TimeSpan.FromMilliseconds(30)).CheckLatestAsync(null);
        Verify(timeout.Status == UpdateCheckStatus.Timeout,
            "Pages request timeout is classified without retrying");

        var newerCache = CachedResult("1.4.0", "\"newer\"");
        var stale = await CreateService(Handler(ManifestJson(version: "1.3.0")), "1.0.0")
            .CheckLatestAsync(newerCache);
        Verify(stale.FromCache && stale.Version == "1.4.0",
            "older Pages manifest cannot replace newer validated stable metadata");

        var service = CreateService(Handler(ManifestJson()));
        var resolved = await service.ResolveAsync(
            "1.2.3", new Uri("https://github.com/Ethereal-09/PyRunner/releases/tag/v1.2.3"));
        Verify(resolved.Success && resolved.Asset is { Version: "1.2.3", Size: 1234 } &&
               resolved.Asset.DownloadUri.Host == "github.com",
            "download metadata is resolved from the same Pages manifest");
        var changed = await CreateService(Handler(ManifestJson(version: "1.2.4"))).ResolveAsync(
            "1.2.3", new Uri("https://github.com/Ethereal-09/PyRunner/releases/tag/v1.2.3"));
        Verify(!changed.Success && changed.Error == UpdatePackageError.AssetChanged,
            "manifest change blocks download and installation revalidation");

        failures += await RunCoordinatorTestsAsync();
        return failures;
    }

    private static IEnumerable<string> InvalidManifests()
    {
        yield return ManifestJson(schemaVersion: 2);
        yield return ManifestJson(version: "1.2", tag: "v1.2");
        yield return ManifestJson(tag: "v1.2.4");
        yield return ManifestJson(draft: true);
        yield return ManifestJson(prerelease: true);
        yield return ManifestJson(assetCount: 0);
        yield return ManifestJson(assetCount: 2);
        yield return ManifestJson(assetUrl: "https://evil.example/PyRunner-Setup-1.2.3-x64.exe");
        yield return ManifestJson(assetUrl: "http://github.com/Ethereal-09/PyRunner/releases/download/v1.2.3/PyRunner-Setup-1.2.3-x64.exe");
        yield return ManifestJson(assetSize: 0);
        yield return ManifestJson(assetSize: GitHubPagesUpdateManifestService.MaximumInstallerBytes + 1);
        yield return ManifestJson(sha256: "bad");
        yield return ManifestJson(fileName: "PyRunner-Setup-1.2.4-x64.exe");
    }

    private static async Task<int> RunCoordinatorTestsAsync()
    {
        var failures = 0;
        void Verify(bool condition, string name)
        {
            if (condition) Console.WriteLine($"PASS: {name}");
            else { failures++; Console.Error.WriteLine($"FAIL: {name}"); }
        }

        var now = DateTimeOffset.Parse("2026-09-08T03:00:00Z");
        var cache = new MemoryCacheStore(new(now.AddHours(-1), CachedResult("1.2.0")));
        var cachedService = new StubUpdateService(UpdateAvailable("1.2.0"));
        using (var coordinator = new UpdateCheckCoordinator(cachedService, cache, new UpdateStateStore(), new FixedTimeProvider(now)))
        {
            var result = await coordinator.CheckAsync(UpdateCheckMode.Automatic);
            Verify(cachedService.CallCount == 0 && result.FromCache,
                "automatic Pages check retains the 24-hour local cache");
            await coordinator.CheckAsync(UpdateCheckMode.Manual);
            Verify(cachedService.CallCount == 1,
                "manual Pages check bypasses the 24-hour attempt cache");
        }

        var shared = new BlockingUpdateService();
        using (var coordinator = new UpdateCheckCoordinator(
            shared, new MemoryCacheStore(), new UpdateStateStore(), new FixedTimeProvider(now)))
        {
            var automatic = coordinator.CheckAsync(UpdateCheckMode.Automatic);
            await shared.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var manual = coordinator.CheckAsync(UpdateCheckMode.Manual);
            shared.Complete(UpdateAvailable("1.3.0"));
            await Task.WhenAll(automatic, manual);
            Verify(shared.CallCount == 1, "concurrent Pages checks share one in-flight request");
        }

        var prior = CachedResult("1.2.0", "\"good\"") with { CheckedAtUtc = now.AddDays(-1) };
        var failedCache = new MemoryCacheStore(new(now.AddDays(-2), prior));
        var state = new UpdateStateStore();
        using (var coordinator = new UpdateCheckCoordinator(
            new StubUpdateService(new(UpdateCheckStatus.Offline)), failedCache, state,
            new FixedTimeProvider(now)))
        {
            await coordinator.CheckAsync(UpdateCheckMode.Automatic);
            Verify(state.Current.LastSuccessfulResult is { Version: "1.2.0", CheckedAtUtc: not null } &&
                   failedCache.Load().LastSuccessfulResult == prior,
                "failed Pages check keeps the timestamped last successful result");
        }
        return failures;
    }

    private static GitHubPagesUpdateManifestService CreateService(
        HttpMessageHandler handler, string localVersion = "1.0.0", TimeSpan? timeout = null)
    {
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(1) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PyRunner");
        return new(client, new ProductLinksOptions(UpdateFeed: ManifestUri), new FixedMetadata(localVersion));
    }

    private static DelegateHandler Handler(string json) =>
        new((_, _) => Task.FromResult(JsonResponse(json)));
    private static DelegateHandler Response(HttpStatusCode status) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status)));
    private static HttpResponseMessage JsonResponse(string json, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static string ManifestJson(
        int schemaVersion = 1, string version = "1.2.3", string? tag = null,
        bool draft = false, bool prerelease = false, int assetCount = 1,
        string? fileName = null, string? assetUrl = null, long assetSize = 1234,
        string? sha256 = null)
    {
        tag ??= $"v{version}";
        fileName ??= $"PyRunner-Setup-{version}-x64.exe";
        assetUrl ??= $"https://github.com/Ethereal-09/PyRunner/releases/download/v{version}/{fileName}";
        sha256 ??= new string('A', 64);
        var asset = new Dictionary<string, object?>
        {
            ["platform"] = "windows", ["architecture"] = "x64", ["fileName"] = fileName,
            ["url"] = assetUrl, ["size"] = assetSize, ["sha256"] = sha256
        };
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = schemaVersion, ["draft"] = draft, ["prerelease"] = prerelease,
            ["version"] = version, ["tag"] = tag, ["publishedAt"] = "2026-09-01T08:00:00Z",
            ["releasePageUrl"] = $"https://github.com/Ethereal-09/PyRunner/releases/tag/v{version}",
            ["releaseNotes"] = "Plain text notes",
            ["assets"] = Enumerable.Range(0, assetCount).Select(_ => asset).ToArray()
        });
    }

    private static UpdateCheckCache CachedResult(string version, string? etag = null) => new(
        version, DateTimeOffset.Parse("2026-09-01T08:00:00Z"), "Cached notes",
        $"https://github.com/Ethereal-09/PyRunner/releases/tag/v{version}", etag,
        DateTimeOffset.Parse("2026-09-01T08:05:00Z"));
    private static UpdateCheckResult UpdateAvailable(string version) => new(
        UpdateCheckStatus.UpdateAvailable, version, DateTimeOffset.Parse("2026-09-01T08:00:00Z"),
        "Notes", new Uri($"https://github.com/Ethereal-09/PyRunner/releases/tag/v{version}"), "\"etag\"");

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            callback(request, cancellationToken);
    }
    private sealed class FixedMetadata(string version) : IAppMetadataService
    {
        public AppMetadata GetSnapshot() => new(
            version, "1", "x64", "stable", "@Ethereal-09", "independent_developer",
            ".NET 8", "Windows App SDK", "Windows", "3.13", "", "xterm.js · ConPTY", "", "stable");
    }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class MemoryCacheStore(UpdateCacheSnapshot? initial = null) : IUpdateCacheStore
    {
        private UpdateCacheSnapshot _snapshot = initial ?? new(null, null);
        public UpdateCacheSnapshot Load() => _snapshot;
        public void SaveAttempt(DateTimeOffset attemptedAtUtc) => _snapshot = _snapshot with { LastAttemptAtUtc = attemptedAtUtc };
        public void SaveSuccessfulResult(UpdateCheckCache result) => _snapshot = _snapshot with { LastSuccessfulResult = result };
    }
    private sealed class StubUpdateService(UpdateCheckResult result) : IUpdateService
    {
        public int CallCount { get; private set; }
        public Task<UpdateCheckResult> CheckLatestAsync(UpdateCheckCache? cachedResult, CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(result); }
        public UpdateCheckResult? RestoreCachedResult(UpdateCheckCache? cachedResult) => cachedResult is null
            ? null
            : UpdateAvailable(cachedResult.Version) with
            { ETag = cachedResult.ETag, FromCache = true, CheckedAtUtc = cachedResult.CheckedAtUtc };
    }
    private sealed class BlockingUpdateService : IUpdateService
    {
        private readonly TaskCompletionSource<UpdateCheckResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public async Task<UpdateCheckResult> CheckLatestAsync(UpdateCheckCache? cachedResult, CancellationToken cancellationToken = default)
        { CallCount++; Started.TrySetResult(); return await _completion.Task.WaitAsync(cancellationToken); }
        public UpdateCheckResult? RestoreCachedResult(UpdateCheckCache? cachedResult) => null;
        public void Complete(UpdateCheckResult result) => _completion.TrySetResult(result);
    }
}
