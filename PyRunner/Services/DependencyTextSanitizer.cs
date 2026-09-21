using System.Text.RegularExpressions;

namespace PyRunner.Services;

internal static partial class DependencyTextSanitizer
{
    public const int MaximumDiagnosticLength = 64 * 1024;

    public static string Sanitize(string? value, int maximumLength = MaximumDiagnosticLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sanitized = UrlWithCredentials().Replace(value, match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[redacted-url]";
            var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
            var safeQuery = QuerySecret().Replace(builder.Query, "$1=[redacted]");
            builder.Query = safeQuery.TrimStart('?');
            return builder.Uri.GetLeftPart(UriPartial.Path) + builder.Query;
        });
        sanitized = AuthorizationCredential().Replace(sanitized, "$1=[redacted]");
        sanitized = InlineSecret().Replace(sanitized, "$1=[redacted]");
        return sanitized.Length <= maximumLength
            ? sanitized
            : sanitized[^maximumLength..];
    }

    public static string SanitizeSource(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return Sanitize(value, 512);
        var host = string.IsNullOrWhiteSpace(uri.Host) ? "local" : uri.Host;
        var scheme = uri.Scheme;
        return $"{scheme}://{host}{uri.AbsolutePath}";
    }

    [GeneratedRegex("(?i)\\b(?:https?|git\\+https?|file)://[^\\s<>\\\"']+")]
    private static partial Regex UrlWithCredentials();

    [GeneratedRegex(@"(?i)([?&](?:token|key|password|passwd|secret|signature|sig|auth|authorization))=[^&\s]*")]
    private static partial Regex QuerySecret();

    [GeneratedRegex(@"(?i)\b(token|key|password|passwd|secret|authorization)\s*[:=]\s*[^\s,;]+")]
    private static partial Regex InlineSecret();

    [GeneratedRegex(@"(?i)\b(authorization)\s*[:=]\s*(?:bearer|basic)\s+[^\s,;]+")]
    private static partial Regex AuthorizationCredential();
}
