using System.Text.RegularExpressions;

namespace PyRunner.Services;

internal static partial class ProductVersionParser
{
    public static string? NormalizeInformationalVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
        var value = informationalVersion.Split('+', 2)[0].Trim();
        if (!ProductVersionPattern().IsMatch(value) || !Version.TryParse(value, out _))
            return null;
        return value;
    }

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductVersionPattern();
}
