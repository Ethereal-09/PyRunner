using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>从固定 GitHub Pages 地址匿名读取稳定版清单，不调用 GitHub REST API。</summary>
public sealed partial class GitHubPagesUpdateManifestService : IUpdateService, IUpdateReleaseAssetService
{
    public const int SupportedSchemaVersion = 1;
    public const int MaximumReleaseNotesLength = 8_000;
    public const long MaximumManifestBytes = 64 * 1024;
    public const long MaximumInstallerBytes = 256L * 1024 * 1024;

    private readonly HttpClient _client;
    private readonly ProductLinksOptions _links;
    private readonly IAppMetadataService _metadata;

    public GitHubPagesUpdateManifestService(
        HttpClient client,
        ProductLinksOptions links,
        IAppMetadataService metadata)
    {
        _client = client;
        _links = links;
        _metadata = metadata;
    }

    public async Task<UpdateCheckResult> CheckLatestAsync(
        UpdateCheckCache? cachedResult,
        CancellationToken cancellationToken = default)
    {
        if (!IsExpectedManifestUri(_links.UpdateFeed))
            return new(UpdateCheckStatus.Failed);

        try
        {
            var sendEtag = !string.IsNullOrWhiteSpace(cachedResult?.ETag) &&
                           EntityTagHeaderValue.TryParse(cachedResult.ETag, out _);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = CreateManifestRequest(_links.UpdateFeed!, sendEtag ? cachedResult?.ETag : null);
                using var response = await _client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    var restored = RestoreCachedResult(cachedResult);
                    if (restored is not null) return restored;
                    if (sendEtag && attempt == 0)
                    {
                        sendEtag = false;
                        continue;
                    }
                    return new(UpdateCheckStatus.InvalidReleaseData);
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return new(UpdateCheckStatus.NoPublishedRelease);
                if ((int)response.StatusCode == 429)
                    return new(UpdateCheckStatus.RateLimited, RateLimitResetAt: ReadRetryAfter(response));
                if (IsRedirect(response.StatusCode) || response.StatusCode != HttpStatusCode.OK)
                    return new(UpdateCheckStatus.Failed);

                var bytes = await ReadBoundedAsync(response, cancellationToken);
                if (!TryParseManifest(bytes, out var manifest))
                    return new(UpdateCheckStatus.InvalidReleaseData);

                var cachedVersion = RestoreCachedResult(cachedResult);
                if (cachedVersion?.Version is { } prior &&
                    TryNormalizeVersion(prior, out var normalizedPrior) &&
                    Version.Parse(normalizedPrior) > Version.Parse(manifest.Version))
                    return cachedVersion;

                return ToCheckResult(manifest, response.Headers.ETag?.ToString());
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(UpdateCheckStatus.Timeout);
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex) when (IsOffline(ex))
        {
            return new(UpdateCheckStatus.Offline);
        }
        catch (HttpRequestException)
        {
            return new(UpdateCheckStatus.Failed);
        }
        catch (Exception)
        {
            return new(UpdateCheckStatus.InvalidReleaseData);
        }

        return new(UpdateCheckStatus.InvalidReleaseData);
    }

    public async Task<UpdateAssetResult> ResolveAsync(
        string version,
        Uri releasePageUri,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeVersion(version, out var normalized) ||
            !IsExpectedReleasePage(releasePageUri, normalized) ||
            !IsExpectedManifestUri(_links.UpdateFeed))
            return new(false, Error: UpdatePackageError.InvalidRelease);

        try
        {
            using var request = CreateManifestRequest(_links.UpdateFeed!, null);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await _client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode == 429)
                return new(false, Error: UpdatePackageError.Network);
            if (response.StatusCode != HttpStatusCode.OK || IsRedirect(response.StatusCode))
                return new(false, Error: UpdatePackageError.Network);

            var bytes = await ReadBoundedAsync(response, cancellationToken);
            if (!TryParseManifest(bytes, out var manifest) ||
                !string.Equals(manifest.Version, normalized, StringComparison.Ordinal) ||
                manifest.ReleasePageUri != releasePageUri)
                return new(false, Error: UpdatePackageError.AssetChanged);
            return new(true, manifest.Asset);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, Error: UpdatePackageError.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return new(false, Error: UpdatePackageError.Timeout);
        }
        catch (HttpRequestException)
        {
            return new(false, Error: UpdatePackageError.Network);
        }
        catch
        {
            return new(false, Error: UpdatePackageError.InvalidRelease);
        }
    }

    public UpdateCheckResult? RestoreCachedResult(UpdateCheckCache? cachedResult)
    {
        if (cachedResult is null || !TryNormalizeVersion(cachedResult.Version, out var version) ||
            !TryGetCurrentVersion(out var currentVersion) ||
            !Uri.TryCreate(cachedResult.ReleasePageUrl, UriKind.Absolute, out var releaseUri) ||
            !IsExpectedReleasePage(releaseUri, version))
            return null;

        return new(
            Version.Parse(version) > currentVersion ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.Latest,
            version,
            cachedResult.PublishedAt,
            LimitUntrustedText(cachedResult.ReleaseNotes),
            releaseUri,
            cachedResult.ETag,
            FromCache: true,
            CheckedAtUtc: cachedResult.CheckedAtUtc);
    }

    internal static bool TryParseManifest(byte[] utf8Json, out UpdateManifest manifest)
    {
        manifest = null!;
        if (utf8Json.Length == 0 || utf8Json.LongLength > MaximumManifestBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !HasExactProperties(root,
                    "schemaVersion", "draft", "prerelease", "version", "tag", "publishedAt",
                    "releasePageUrl", "releaseNotes", "assets") ||
                !TryReadInt(root, "schemaVersion", out var schema) || schema != SupportedSchemaVersion ||
                !IsExplicitFalse(root, "draft") || !IsExplicitFalse(root, "prerelease") ||
                !TryNormalizeVersion(ReadString(root, "version"), out var version) ||
                !string.Equals(ReadString(root, "tag"), $"v{version}", StringComparison.Ordinal) ||
                !root.TryGetProperty("publishedAt", out var publishedValue) ||
                publishedValue.ValueKind != JsonValueKind.String ||
                !publishedValue.TryGetDateTimeOffset(out var publishedAt) ||
                publishedAt.Offset != TimeSpan.Zero ||
                !Uri.TryCreate(ReadString(root, "releasePageUrl"), UriKind.Absolute, out var releasePageUri) ||
                !IsExpectedReleasePage(releasePageUri, version) ||
                !root.TryGetProperty("releaseNotes", out var notesValue) ||
                notesValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() != 1)
                return false;

            var assetValue = assets[0];
            var expectedFileName = $"PyRunner-Setup-{version}-x64.exe";
            if (assetValue.ValueKind != JsonValueKind.Object ||
                !HasExactProperties(assetValue,
                    "platform", "architecture", "fileName", "url", "size", "sha256") ||
                !string.Equals(ReadString(assetValue, "platform"), "windows", StringComparison.Ordinal) ||
                !string.Equals(ReadString(assetValue, "architecture"), "x64", StringComparison.Ordinal) ||
                !string.Equals(ReadString(assetValue, "fileName"), expectedFileName, StringComparison.Ordinal) ||
                !assetValue.TryGetProperty("size", out var sizeValue) ||
                !sizeValue.TryGetInt64(out var size) || size <= 0 || size > MaximumInstallerBytes ||
                !Uri.TryCreate(ReadString(assetValue, "url"), UriKind.Absolute, out var downloadUri) ||
                !IsExpectedAssetUri(downloadUri, version, expectedFileName) ||
                ReadString(assetValue, "sha256") is not { } sha256 || !Sha256Pattern().IsMatch(sha256))
                return false;

            var notes = LimitUntrustedText(notesValue.GetString());
            var asset = new UpdateReleaseAsset(
                0, version, expectedFileName, size, downloadUri,
                sha256.ToUpperInvariant(), publishedAt, releasePageUri);
            manifest = new UpdateManifest(
                schema, version, $"v{version}", publishedAt, notes, releasePageUri, asset);
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private UpdateCheckResult ToCheckResult(UpdateManifest manifest, string? etag)
    {
        if (!TryGetCurrentVersion(out var currentVersion))
            return new(UpdateCheckStatus.InvalidReleaseData);
        return new(
            Version.Parse(manifest.Version) > currentVersion
                ? UpdateCheckStatus.UpdateAvailable
                : UpdateCheckStatus.Latest,
            manifest.Version,
            manifest.PublishedAt,
            manifest.ReleaseNotes,
            manifest.ReleasePageUri,
            etag);
    }

    private static HttpRequestMessage CreateManifestRequest(Uri uri, string? etag)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(etag) && EntityTagHeaderValue.TryParse(etag, out var parsed))
            request.Headers.IfNoneMatch.Add(parsed);
        return request;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumManifestBytes)
            throw new JsonException("Update manifest is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaximumManifestBytes)
                throw new JsonException("Update manifest is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public static bool IsExpectedManifestUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true, IsDefaultPort: true } &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, "ethereal-09.github.io", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.AbsolutePath, "/PyRunner/update.json", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) && !uri.OriginalString.Contains('%') &&
        !uri.OriginalString.Contains('\\');

    public static bool IsAllowedReleaseUri(Uri? uri) =>
        TryGetReleaseUriVersion(uri, out _);

    public static bool IsExpectedAssetUri(Uri? uri, string version, string fileName) =>
        uri is { IsAbsoluteUri: true, IsDefaultPort: true } &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) && !uri.OriginalString.Contains('%') &&
        !uri.OriginalString.Contains('\\') &&
        string.Equals(uri.AbsolutePath,
            $"/Ethereal-09/PyRunner/releases/download/v{version}/{fileName}", StringComparison.Ordinal);

    private static bool IsExpectedReleasePage(Uri uri, string version) =>
        TryGetReleaseUriVersion(uri, out var parsed) &&
        string.Equals(parsed, version, StringComparison.Ordinal);

    private static bool TryGetReleaseUriVersion(Uri? uri, out string version)
    {
        version = string.Empty;
        if (uri is not { IsAbsoluteUri: true, IsDefaultPort: true } ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.OriginalString.Contains('%') ||
            uri.OriginalString.Contains('\\')) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 5 ||
            !string.Equals(segments[0], "Ethereal-09", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], "PyRunner", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[2], "releases", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[3], "tag", StringComparison.OrdinalIgnoreCase)) return false;
        return TryNormalizeTag(segments[4], out version);
    }

    private bool TryGetCurrentVersion(out Version version)
    {
        version = new Version(0, 0, 0);
        if (!TryNormalizeVersion(_metadata.GetSnapshot().Version, out var normalized) ||
            !Version.TryParse(normalized, out var parsed) || parsed is null) return false;
        version = parsed;
        return true;
    }

    internal static bool TryNormalizeVersion(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !VersionPattern().IsMatch(value)) return false;
        if (!Version.TryParse(value, out var version) || version is null) return false;
        normalized = $"{version.Major}.{version.Minor}.{version.Build}";
        return string.Equals(value, normalized, StringComparison.Ordinal);
    }

    private static bool TryNormalizeTag(string? value, out string version)
    {
        version = string.Empty;
        return value is not null && value.StartsWith('v') &&
               TryNormalizeVersion(value[1..], out version) &&
               string.Equals(value, $"v{version}", StringComparison.Ordinal);
    }

    private static string LimitUntrustedText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(Math.Min(value.Length, MaximumReleaseNotesLength));
        foreach (var character in value)
            if (character is '\r' or '\n' or '\t' || !char.IsControl(character)) builder.Append(character);
        var result = builder.ToString().Trim();
        return result.Length > MaximumReleaseNotesLength
            ? result[..(MaximumReleaseNotesLength - 1)] + "…"
            : result;
    }

    private static bool IsExplicitFalse(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;
    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }
    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
               expected.All(name => names.Contains(name, StringComparer.Ordinal));
    }
    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static DateTimeOffset? ReadRetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Date ??
        (response.Headers.RetryAfter?.Delta is { } delta ? DateTimeOffset.UtcNow.Add(delta) : null);
    private static bool IsOffline(HttpRequestException exception) =>
        exception.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError ||
        exception.InnerException is SocketException;

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}

public sealed record UpdateManifest(
    int SchemaVersion,
    string Version,
    string Tag,
    DateTimeOffset PublishedAt,
    string ReleaseNotes,
    Uri ReleasePageUri,
    UpdateReleaseAsset Asset);
