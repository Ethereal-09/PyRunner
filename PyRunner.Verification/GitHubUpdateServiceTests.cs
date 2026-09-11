using System.Net;
using System.Net.Http.Headers;
using PyRunner.Models;
using PyRunner.Services;

internal static class GitHubUpdateServiceTests
{
    private static readonly Uri LatestReleaseApi =
        new("https://api.github.com/repos/Ethereal-09/PyRunner/releases/latest");

    public static async Task<int> RunAsync()
    {
        var failures = 0;

        void Verify(bool condition, string name, string? details = null)
        {
            if (condition)
            {
                Console.WriteLine($"PASS: {name}");
                return;
            }

            failures++;
            Console.Error.WriteLine($"FAIL: {name}{(string.IsNullOrWhiteSpace(details) ? string.Empty : $" ({details})")}");
        }

        var newVersionHandler = new DelegateHandler((request, _) =>
        {
            VerifyRequestHeaders(request, Verify);
            return Task.FromResult(JsonResponse(ReleaseJson("v1.2.3"), "\"release-123\""));
        });
        var newVersion = await CreateService(newVersionHandler).CheckLatestAsync(null);
        Verify(newVersion.Status == UpdateCheckStatus.UpdateAvailable &&
               newVersion.Version == "1.2.3" &&
               newVersion.ReleasePageUri?.Scheme == Uri.UriSchemeHttps &&
               newVersion.ETag == "\"release-123\"",
            "200 release reports a newer stable version and preserves ETag");

        var latest = await CreateService(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(ReleaseJson("1.0.0"))))).CheckLatestAsync(null);
        Verify(latest.Status == UpdateCheckStatus.Latest && latest.Version == "1.0.0",
            "200 release reports the installed version as current");

        foreach (var tag in new[] { "v1.2.3", "V1.2.3", "1.2.3" })
        {
            var prefixResult = await CreateService(new DelegateHandler((_, _) =>
                Task.FromResult(JsonResponse(ReleaseJson(tag))))).CheckLatestAsync(null);
            Verify(prefixResult.Status == UpdateCheckStatus.UpdateAvailable && prefixResult.Version == "1.2.3",
                $"stable tag format is accepted: {tag}");
        }

        var cached = CachedResult("1.2.0", "\"cached-etag\"");
        var notModifiedHandler = new DelegateHandler((request, _) =>
        {
            Verify(request.Headers.IfNoneMatch.Any(value => value.Tag == "\"cached-etag\""),
                "conditional request sends If-None-Match");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        });
        var notModified = await CreateService(notModifiedHandler).CheckLatestAsync(cached);
        Verify(notModified.Status == UpdateCheckStatus.UpdateAvailable && notModified.FromCache &&
               notModified.Version == "1.2.0",
            "304 reuses the last successful cached release");

        var retryRequests = new List<bool>();
        var retryHandler = new DelegateHandler((request, _) =>
        {
            retryRequests.Add(request.Headers.IfNoneMatch.Count > 0);
            return Task.FromResult(retryRequests.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : JsonResponse(ReleaseJson("v1.2.3"), "\"fresh-etag\""));
        });
        var invalidCacheWithEtag = CachedResult("invalid", "\"stale-etag\"");
        var retried = await CreateService(retryHandler).CheckLatestAsync(invalidCacheWithEtag);
        Verify(retried.Status == UpdateCheckStatus.UpdateAvailable &&
               retryRequests.SequenceEqual(new[] { true, false }),
            "304 without a valid cache retries once without If-None-Match");

        var repeated304Requests = new List<bool>();
        var repeated304 = await CreateService(new DelegateHandler((request, _) =>
        {
            repeated304Requests.Add(request.Headers.IfNoneMatch.Count > 0);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        })).CheckLatestAsync(invalidCacheWithEtag);
        Verify(repeated304.Status == UpdateCheckStatus.InvalidReleaseData &&
               repeated304Requests.SequenceEqual(new[] { true, false }),
            "a second 304 is invalid and does not trigger an infinite retry");

        var notFound = await CheckStatusAsync(new HttpResponseMessage(HttpStatusCode.NotFound));
        Verify(notFound == UpdateCheckStatus.NoPublishedRelease,
            "404 is classified as no published release");

        var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden);
        forbidden.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
        forbidden.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "1788230400");
        var forbiddenResult = await CheckResultAsync(forbidden);
        Verify(forbiddenResult.Status == UpdateCheckStatus.RateLimited && forbiddenResult.RateLimitResetAt is not null,
            "403 with exhausted GitHub quota is classified as rate limited");

        var tooMany = await CheckStatusAsync(new HttpResponseMessage((HttpStatusCode)429));
        Verify(tooMany == UpdateCheckStatus.RateLimited,
            "429 is classified as rate limited");

        var timeout = await CreateService(new DelegateHandler((_, _) =>
            throw new TaskCanceledException("simulated timeout"))).CheckLatestAsync(null);
        Verify(timeout.Status == UpdateCheckStatus.Timeout,
            "transport timeout is classified as timeout");

        var offline = await CreateService(new DelegateHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "simulated offline")))
            .CheckLatestAsync(null);
        Verify(offline.Status == UpdateCheckStatus.Offline,
            "name resolution failure is classified as offline");

        var invalidJson = await CreateService(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse("{not-json")))).CheckLatestAsync(null);
        Verify(invalidJson.Status == UpdateCheckStatus.InvalidReleaseData,
            "malformed JSON is classified as invalid release data");

        var missingTag = await CreateService(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(ReleaseJson(null))))).CheckLatestAsync(null);
        Verify(missingTag.Status == UpdateCheckStatus.InvalidReleaseData,
            "missing tag_name is classified as invalid release data");

        var missingUrl = await CreateService(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(ReleaseJson("1.2.3", includeUrl: false))))).CheckLatestAsync(null);
        Verify(missingUrl.Status == UpdateCheckStatus.InvalidReleaseData,
            "missing html_url is classified as invalid release data");

        foreach (var tag in new[] { "1.2", "v1.2.3-beta", "release-1.2.3", "1.2.3.4", "v١.٢.٣" })
        {
            var invalidVersion = await CreateService(new DelegateHandler((_, _) =>
                Task.FromResult(JsonResponse(ReleaseJson(tag))))).CheckLatestAsync(null);
            Verify(invalidVersion.Status == UpdateCheckStatus.InvalidReleaseData,
                $"invalid stable release tag is rejected: {tag}");
        }

        var draft = await CreateService(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(ReleaseJson("1.2.3", draft: true))))).CheckLatestAsync(null);
        var prerelease = await CreateService(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(ReleaseJson("1.2.3", prerelease: true))))).CheckLatestAsync(null);
        Verify(draft.Status == UpdateCheckStatus.NoPublishedRelease &&
               prerelease.Status == UpdateCheckStatus.NoPublishedRelease,
            "draft and prerelease payloads are ignored by the stable channel");

        var longNotes = new string('a', 8_100) + "\u0001<script>ignored as markup</script>";
        var notesResult = await CreateService(new DelegateHandler((_, _) =>
            Task.FromResult(JsonResponse(ReleaseJson("1.2.3", body: longNotes))))).CheckLatestAsync(null);
        Verify(notesResult.ReleaseNotes is { Length: 8_000 } &&
               !notesResult.ReleaseNotes.Contains('\u0001'),
            "release notes are treated as bounded plain text");

        foreach (var body in new string?[] { null, string.Empty })
        {
            var emptyNotes = await CreateService(new DelegateHandler((_, _) =>
                Task.FromResult(JsonResponse(ReleaseJson("1.2.3", body: body))))).CheckLatestAsync(null);
            Verify(emptyNotes.ReleaseNotes == string.Empty,
                $"null or empty release notes are handled: {body is null}");
        }

        var rejectedUris = new[]
        {
            "https://github.com.evil.example/Ethereal-09/PyRunner/releases/tag/v1.2.3",
            "https://user:pass@github.com/Ethereal-09/PyRunner/releases/tag/v1.2.3",
            "https://github.com:444/Ethereal-09/PyRunner/releases/tag/v1.2.3",
            "https://github.com/Ethereal-09/Other/releases/tag/v1.2.3",
            "https://github.com/Ethereal-09/PyRunner/releases/download/v1.2.3",
            "https://github.com/Ethereal-09/PyRunner/releases/tag%2Fv1.2.3",
            "https://github.com/Ethereal-09/PyRunner/releases/tag/%2e%2e/v1.2.3",
            "https://github.com/Ethereal-09/PyRunner/releases/tag/v1.2.3?redirect=evil",
            "http://github.com/Ethereal-09/PyRunner/releases/tag/v1.2.3",
            "file:///Ethereal-09/PyRunner/releases/tag/v1.2.3",
            "javascript:alert(1)",
        };
        foreach (var value in rejectedUris)
        {
            Verify(!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                   !GitHubUpdateService.IsAllowedReleaseUri(uri),
                $"malicious release URI is rejected: {value}");
        }
        Verify(GitHubUpdateService.IsAllowedReleaseUri(
                new Uri("https://github.com/Ethereal-09/PyRunner/releases/tag/v1.2.3")),
            "canonical GitHub release tag URI is accepted");

        failures += await RunCoordinatorTestsAsync();
        return failures;
    }

    private static async Task<int> RunCoordinatorTestsAsync()
    {
        var failures = 0;
        void Verify(bool condition, string name)
        {
            if (condition) Console.WriteLine($"PASS: {name}");
            else
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {name}");
            }
        }

        var now = DateTimeOffset.Parse("2026-09-08T03:00:00Z");
        var cache = new MemoryCacheStore(new UpdateCacheSnapshot(now.AddHours(-1), CachedResult("1.2.0")));
        var cachedService = new StubUpdateService(UpdateAvailable("1.2.0"));
        using (var coordinator = new UpdateCheckCoordinator(cachedService, cache, new UpdateStateStore(), new FixedTimeProvider(now)))
        {
            var result = await coordinator.CheckAsync(UpdateCheckMode.Automatic);
            Verify(cachedService.CallCount == 0 && result.FromCache && result.Status == UpdateCheckStatus.UpdateAvailable,
                "automatic check reuses a last attempt made within 24 hours");

            await coordinator.CheckAsync(UpdateCheckMode.Manual);
            Verify(cachedService.CallCount == 1,
                "manual check bypasses the 24-hour automatic cache");
        }

        var sharedService = new BlockingUpdateService();
        using (var coordinator = new UpdateCheckCoordinator(
            sharedService, new MemoryCacheStore(), new UpdateStateStore(), new FixedTimeProvider(now)))
        {
            var automatic = coordinator.CheckAsync(UpdateCheckMode.Automatic);
            await sharedService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var manual = coordinator.CheckAsync(UpdateCheckMode.Manual);
            sharedService.Complete(UpdateAvailable("1.3.0"));
            var results = await Task.WhenAll(automatic, manual);
            Verify(sharedService.CallCount == 1 && ReferenceEquals(results[0], results[1]),
                "automatic and manual checks share one in-flight network task");
        }

        var priorSuccess = CachedResult("1.2.0", "\"good-etag\"");
        var failureCache = new MemoryCacheStore(new UpdateCacheSnapshot(now.AddDays(-2), priorSuccess));
        var failureState = new UpdateStateStore();
        using (var coordinator = new UpdateCheckCoordinator(
            new StubUpdateService(new UpdateCheckResult(UpdateCheckStatus.Offline)),
            failureCache,
            failureState,
            new FixedTimeProvider(now)))
        {
            var result = await coordinator.CheckAsync(UpdateCheckMode.Manual);
            Verify(result.Status == UpdateCheckStatus.Offline &&
                   failureState.Current.LastSuccessfulResult?.Version == "1.2.0" &&
                   failureCache.Load().LastSuccessfulResult == priorSuccess &&
                   failureCache.Load().LastAttemptAtUtc == now,
                "network failure records the attempt without overwriting the last successful result");
        }

        var cancellableService = new BlockingUpdateService();
        var closingCoordinator = new UpdateCheckCoordinator(
            cancellableService, new MemoryCacheStore(), new UpdateStateStore(), new FixedTimeProvider(now));
        var closingRequest = closingCoordinator.CheckAsync(UpdateCheckMode.Manual);
        await cancellableService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        closingCoordinator.Dispose();
        var canceledOnClose = false;
        try { await closingRequest; }
        catch (OperationCanceledException) { canceledOnClose = true; }
        Verify(canceledOnClose,
            "disposing the coordinator cancels and safely observes an in-flight HTTP request");

        return failures;
    }

    private static GitHubUpdateService CreateService(HttpMessageHandler handler, string localVersion = "1.0.0")
    {
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(1),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PyRunner");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return new GitHubUpdateService(
            client,
            new ProductLinksOptions(UpdateFeed: LatestReleaseApi),
            new FixedMetadata(localVersion));
    }

    private static Task<UpdateCheckStatus> CheckStatusAsync(HttpResponseMessage response) =>
        CheckResultAsync(response).ContinueWith(task => task.Result.Status, TaskScheduler.Default);

    private static async Task<UpdateCheckResult> CheckResultAsync(HttpResponseMessage response) =>
        await CreateService(new DelegateHandler((_, _) => Task.FromResult(response))).CheckLatestAsync(null);

    private static HttpResponseMessage JsonResponse(string json, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        };
        if (etag is not null)
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static string ReleaseJson(
        string? tag,
        bool includeUrl = true,
        bool draft = false,
        bool prerelease = false,
        string? body = "Plain text notes")
    {
        var tagProperty = tag is null ? string.Empty : $"\"tag_name\":{System.Text.Json.JsonSerializer.Serialize(tag)},";
        var urlTag = string.IsNullOrWhiteSpace(tag) ? "v1.2.3" : tag;
        var urlProperty = includeUrl
            ? $"\"html_url\":\"https://github.com/Ethereal-09/PyRunner/releases/tag/{urlTag}\","
            : string.Empty;
        return $"{{{tagProperty}{urlProperty}\"published_at\":\"2026-09-01T08:00:00Z\",\"body\":{System.Text.Json.JsonSerializer.Serialize(body)},\"draft\":{draft.ToString().ToLowerInvariant()},\"prerelease\":{prerelease.ToString().ToLowerInvariant()}}}";
    }

    private static UpdateCheckCache CachedResult(string version, string? etag = null) => new(
        version,
        DateTimeOffset.Parse("2026-09-01T08:00:00Z"),
        "Cached notes",
        $"https://github.com/Ethereal-09/PyRunner/releases/tag/v{version}",
        etag,
        DateTimeOffset.Parse("2026-09-01T08:05:00Z"));

    private static UpdateCheckResult UpdateAvailable(string version) => new(
        UpdateCheckStatus.UpdateAvailable,
        version,
        DateTimeOffset.Parse("2026-09-01T08:00:00Z"),
        "Notes",
        new Uri($"https://github.com/Ethereal-09/PyRunner/releases/tag/v{version}"),
        "\"etag\"");

    private static void VerifyRequestHeaders(
        HttpRequestMessage request,
        Action<bool, string, string?> verify)
    {
        verify(request.RequestUri == LatestReleaseApi, "fixed GitHub latest-release API is requested", request.RequestUri?.ToString());
        verify(request.Headers.UserAgent.ToString() == "PyRunner", "GitHub User-Agent header is configured", request.Headers.UserAgent.ToString());
        verify(request.Headers.Accept.Any(value => value.MediaType == "application/vnd.github+json"),
            "GitHub media type header is configured", null);
        verify(request.Headers.TryGetValues("X-GitHub-Api-Version", out var values) && values.Contains("2022-11-28"),
            "GitHub API version header is configured", null);
    }

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
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MemoryCacheStore(UpdateCacheSnapshot? initial = null) : IUpdateCacheStore
    {
        private UpdateCacheSnapshot _snapshot = initial ?? new(null, null);
        public UpdateCacheSnapshot Load() => _snapshot;
        public void SaveAttempt(DateTimeOffset attemptedAtUtc) =>
            _snapshot = _snapshot with { LastAttemptAtUtc = attemptedAtUtc };
        public void SaveSuccessfulResult(UpdateCheckCache result) =>
            _snapshot = _snapshot with { LastSuccessfulResult = result };
    }

    private sealed class StubUpdateService(UpdateCheckResult result) : IUpdateService
    {
        public int CallCount { get; private set; }
        public Task<UpdateCheckResult> CheckLatestAsync(UpdateCheckCache? cachedResult, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(result);
        }

        public UpdateCheckResult? RestoreCachedResult(UpdateCheckCache? cachedResult) =>
            cachedResult is null
                ? null
                : UpdateAvailable(cachedResult.Version) with { ETag = cachedResult.ETag, FromCache = true };
    }

    private sealed class BlockingUpdateService : IUpdateService
    {
        private readonly TaskCompletionSource<UpdateCheckResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public async Task<UpdateCheckResult> CheckLatestAsync(
            UpdateCheckCache? cachedResult,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult();
            return await _completion.Task.WaitAsync(cancellationToken);
        }

        public UpdateCheckResult? RestoreCachedResult(UpdateCheckCache? cachedResult) => null;
        public void Complete(UpdateCheckResult result) => _completion.TrySetResult(result);
    }
}
