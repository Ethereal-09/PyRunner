using PyRunner.Models;

namespace PyRunner.Services.AI;

public static class AiEndpointPolicy
{
    public static Uri Normalize(string value, AiProviderKind provider, bool allowInsecureLoopback = false)
    {
        var fallback = provider == AiProviderKind.OpenAI ? "https://api.openai.com/v1/" : value;
        if (!Uri.TryCreate(fallback?.Trim(), UriKind.Absolute, out var uri))
            throw new AiProviderException(AiErrorKind.InvalidConfiguration, "AI_Error_InvalidEndpoint");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query))
            throw new AiProviderException(AiErrorKind.InvalidConfiguration, "AI_Error_InvalidEndpoint");
        var isLoopback = uri.IsLoopback && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            System.Net.IPAddress.TryParse(uri.Host, out var address) && System.Net.IPAddress.IsLoopback(address));
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            !(allowInsecureLoopback && isLoopback && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            throw new AiProviderException(AiErrorKind.InvalidConfiguration, "AI_Error_HttpsRequired");
        if (provider == AiProviderKind.OpenAI &&
            (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             !uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)))
            throw new AiProviderException(AiErrorKind.InvalidConfiguration, "AI_Error_InvalidEndpoint");
        var builder = new UriBuilder(uri) { Fragment = string.Empty, Query = string.Empty };
        if (!builder.Path.EndsWith('/')) builder.Path += "/";
        return builder.Uri;
    }

    public static Uri ResponsesUri(AiConfiguration configuration) => new(configuration.Endpoint, "responses");
}
