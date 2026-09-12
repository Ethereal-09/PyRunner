using System.Net;
using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>只允许 GitHub 官方 HTTPS 资产链路，并显式逐跳验证重定向。</summary>
public sealed class TrustedUpdateHttpClient : IDisposable
{
    private const int MaximumRedirects = 5;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public TrustedUpdateHttpClient(HttpClient client, bool ownsClient = false)
    {
        _client = client;
        _ownsClient = ownsClient;
    }

    public static TrustedUpdateHttpClient CreateDefault()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PyRunner");
        return new TrustedUpdateHttpClient(client, ownsClient: true);
    }

    public async Task<byte[]> GetBytesAsync(Uri uri, long maximumBytes, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
            throw new UpdateTransportException(UpdatePackageError.FileTooLarge);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new UpdateTransportException(UpdatePackageError.FileTooLarge);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    public async Task<long> DownloadAsync(
        Uri uri,
        string destination,
        long expectedBytes,
        long maximumBytes,
        IProgress<Models.UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength &&
            (contentLength > maximumBytes || contentLength != expectedBytes))
            throw new UpdateTransportException(contentLength > maximumBytes
                ? UpdatePackageError.FileTooLarge
                : UpdatePackageError.SizeMismatch);

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        long received = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            received += read;
            if (received > maximumBytes || received > expectedBytes)
                throw new UpdateTransportException(received > maximumBytes
                    ? UpdatePackageError.FileTooLarge
                    : UpdatePackageError.SizeMismatch);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            progress?.Report(new Models.UpdateDownloadProgress(received, expectedBytes));
        }
        await output.FlushAsync(cancellationToken);
        if (received != expectedBytes)
            throw new UpdateTransportException(UpdatePackageError.SizeMismatch);
        return received;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        var current = initialUri;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            if (!IsTrustedUri(current))
                throw new UpdateTransportException(UpdatePackageError.UntrustedAddress);

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!IsRedirect(response.StatusCode)) return response;

            if (redirect == MaximumRedirects || response.Headers.Location is not { } location)
            {
                response.Dispose();
                throw new UpdateTransportException(UpdatePackageError.UntrustedAddress);
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            response.Dispose();
            if (!IsTrustedUri(next))
                throw new UpdateTransportException(UpdatePackageError.UntrustedAddress);
            current = next;
        }
        throw new UpdateTransportException(UpdatePackageError.UntrustedAddress);
    }

    public static bool IsTrustedUri(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true } ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.OriginalString.Contains('\\'))
            return false;

        return string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(uri.Host, "github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}

public sealed class UpdateTransportException : Exception
{
    public UpdatePackageError Error { get; }
    public UpdateTransportException(UpdatePackageError error) : base(error.ToString()) => Error = error;
}
