using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>匿名读取 GitHub 最新稳定 Release；不下载、不执行任何发布资产。</summary>
public sealed partial class GitHubUpdateService : IUpdateService
{
    private const int MaximumReleaseNotesLength = 8_000;

    private readonly HttpClient _client;
    private readonly ProductLinksOptions _links;
    private readonly IAppMetadataService _metadata;

    public GitHubUpdateService(HttpClient client, ProductLinksOptions links, IAppMetadataService metadata)
    {
        _client = client;
        _links = links;
        _metadata = metadata;
    }

    public async Task<UpdateCheckResult> CheckLatestAsync(
        UpdateCheckCache? cachedResult,
        CancellationToken cancellationToken = default)
    {
        if (_links.UpdateFeed is null)
            return new(UpdateCheckStatus.Failed);

        try
        {
            var sendEtag = !string.IsNullOrWhiteSpace(cachedResult?.ETag) &&
                           EntityTagHeaderValue.TryParse(cachedResult.ETag, out _);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _links.UpdateFeed);
                if (sendEtag && EntityTagHeaderValue.TryParse(cachedResult!.ETag, out var etag))
                    request.Headers.IfNoneMatch.Add(etag);

                using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    var restored = RestoreCachedResult(cachedResult);
                    if (restored is not null) return restored;

                    // 仅当首个请求实际携带 ETag 时，无条件重试一次。第二个请求
                    // 不携带 If-None-Match，且即使再次收到 304 也不会继续重试。
                    if (sendEtag && attempt == 0)
                    {
                        sendEtag = false;
                        continue;
                    }
                    return new(UpdateCheckStatus.InvalidReleaseData);
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                    return new(UpdateCheckStatus.NoPublishedRelease);

                if ((int)response.StatusCode == 429 || IsRateLimited(response))
                    return new(
                        UpdateCheckStatus.RateLimited,
                        RateLimitResetAt: ReadRateLimitReset(response));

                if (response.StatusCode != HttpStatusCode.OK)
                    return new(UpdateCheckStatus.Failed);

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                return ParseRelease(
                    json.RootElement,
                    response.Headers.ETag?.ToString());
            }

            return new(UpdateCheckStatus.InvalidReleaseData);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(UpdateCheckStatus.Timeout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex) when (IsOffline(ex))
        {
            return new(UpdateCheckStatus.Offline);
        }
        catch (HttpRequestException)
        {
            return new(UpdateCheckStatus.Failed);
        }
        catch (JsonException)
        {
            return new(UpdateCheckStatus.InvalidReleaseData);
        }
        catch (Exception)
        {
            return new(UpdateCheckStatus.Failed);
        }
    }

    public UpdateCheckResult? RestoreCachedResult(UpdateCheckCache? cachedResult)
    {
        if (cachedResult is null ||
            !TryParseStableVersion(cachedResult.Version, out var remoteVersion) ||
            !TryGetCurrentVersion(out var currentVersion) ||
            !Uri.TryCreate(cachedResult.ReleasePageUrl, UriKind.Absolute, out var releaseUri) ||
            !TryGetReleaseUriVersion(releaseUri, out var releaseUriVersion) ||
            releaseUriVersion != remoteVersion)
        {
            return null;
        }

        return new UpdateCheckResult(
            remoteVersion > currentVersion ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.Latest,
            cachedResult.Version,
            cachedResult.PublishedAt,
            LimitUntrustedText(cachedResult.ReleaseNotes),
            releaseUri,
            cachedResult.ETag,
            FromCache: true);
    }

    private UpdateCheckResult ParseRelease(JsonElement root, string? etag)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new(UpdateCheckStatus.InvalidReleaseData);

        if ((root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) ||
            (root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True))
        {
            return new(UpdateCheckStatus.NoPublishedRelease);
        }

        var tag = root.TryGetProperty("tag_name", out var tagValue) && tagValue.ValueKind == JsonValueKind.String
            ? tagValue.GetString()
            : null;
        if (!TryParseStableVersion(tag, out var remoteVersion) || !TryGetCurrentVersion(out var currentVersion))
            return new(UpdateCheckStatus.InvalidReleaseData);

        var releaseUrl = root.TryGetProperty("html_url", out var urlValue) && urlValue.ValueKind == JsonValueKind.String
            ? urlValue.GetString()
            : null;
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var releaseUri) ||
            !TryGetReleaseUriVersion(releaseUri, out var releaseUriVersion) ||
            releaseUriVersion != remoteVersion)
            return new(UpdateCheckStatus.InvalidReleaseData);

        if (!root.TryGetProperty("published_at", out var publishedValue) ||
            publishedValue.ValueKind != JsonValueKind.String ||
            !publishedValue.TryGetDateTimeOffset(out var publishedAt))
        {
            return new(UpdateCheckStatus.InvalidReleaseData);
        }

        var rawNotes = root.TryGetProperty("body", out var bodyValue) && bodyValue.ValueKind == JsonValueKind.String
            ? bodyValue.GetString()
            : string.Empty;
        var normalizedVersion = $"{remoteVersion.Major}.{remoteVersion.Minor}.{remoteVersion.Build}";

        return new UpdateCheckResult(
            remoteVersion > currentVersion ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.Latest,
            normalizedVersion,
            publishedAt,
            LimitUntrustedText(rawNotes),
            releaseUri,
            etag);
    }

    private bool TryGetCurrentVersion(out Version version) =>
        TryParseStableVersion(_metadata.GetSnapshot().Version, out version);

    private static bool TryParseStableVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value) || !StableVersionPattern().IsMatch(value.Trim()))
            return false;

        if (!Version.TryParse(value.Trim().TrimStart('v', 'V'), out var parsed) || parsed is null)
            return false;
        version = parsed;
        return true;
    }

    public static bool IsAllowedReleaseUri(Uri? uri) =>
        TryGetReleaseUriVersion(uri, out _);

    private static bool TryGetReleaseUriVersion(Uri? uri, out Version version)
    {
        version = new Version(0, 0, 0);
        if (uri is not { IsAbsoluteUri: true } ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        // Stable tags are ASCII-only, therefore no percent-encoding is needed in a valid path.
        // Rejecting it before decoding closes encoded slash, backslash and traversal variants.
        var original = uri.OriginalString;
        if (original.Contains('%') || original.Contains('\\'))
        {
            return false;
        }

        string decodedPath;
        try
        {
            decodedPath = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        catch (UriFormatException)
        {
            return false;
        }

        var segments = decodedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 5 ||
            !string.Equals(segments[0], "Ethereal-09", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], "PyRunner", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[2], "releases", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[3], "tag", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryParseStableVersion(segments[4], out version);
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden)
            return false;
        return response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) &&
               values.Any(value => string.Equals(value.Trim(), "0", StringComparison.Ordinal));
    }

    private static DateTimeOffset? ReadRateLimitReset(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
            long.TryParse(resetValues.FirstOrDefault(), out var unixSeconds))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(unixSeconds); }
            catch (ArgumentOutOfRangeException) { }
        }

        if (response.Headers.RetryAfter?.Date is { } retryDate)
            return retryDate;
        if (response.Headers.RetryAfter?.Delta is { } retryDelta)
            return DateTimeOffset.UtcNow.Add(retryDelta);
        return null;
    }

    private static bool IsOffline(HttpRequestException exception) =>
        exception.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError ||
        exception.InnerException is SocketException;

    private static string LimitUntrustedText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(Math.Min(value.Length, MaximumReleaseNotesLength));
        foreach (var character in value)
        {
            if (character is '\r' or '\n' or '\t' || !char.IsControl(character))
                builder.Append(character);
        }

        var result = builder.ToString().Trim();
        return result.Length > MaximumReleaseNotesLength
            ? result[..(MaximumReleaseNotesLength - 1)] + "…"
            : result;
    }

    [GeneratedRegex("^[vV]?[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionPattern();
}
