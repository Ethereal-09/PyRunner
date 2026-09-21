using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PyRunner.Models;

namespace PyRunner.Services;

public sealed partial class RequirementsPolicy : IRequirementsPolicy
{
    private const int MaximumDepth = 8;
    private const int MaximumFiles = 64;
    private const long MaximumFileBytes = 1024 * 1024;
    private const long MaximumTotalBytes = 4 * 1024 * 1024;

    public async Task<RequirementsPolicyResult> AnalyzeAsync(
        string scriptPath,
        string interpreterPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedScript = Path.GetFullPath(scriptPath);
        var normalizedInterpreter = Path.GetFullPath(interpreterPath);
        var root = Path.GetDirectoryName(normalizedScript)
            ?? throw new IOException("Script directory is unavailable.");
        var requirements = Path.Combine(root, "requirements.txt");
        if (!File.Exists(requirements))
            return new RequirementsPolicyResult(false, false, false, null, [],
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        var risks = new List<DependencyRisk>();
        var requested = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fingerprints = new List<RequirementsFileFingerprint>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        var blocked = false;
        var requiresConfirmation = false;

        async Task VisitAsync(string path, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > MaximumDepth)
            {
                AddRisk(DependencyRiskSeverity.Blocked, "recursive-depth", path,
                    "Dependency_Risk_RecursiveDepth");
                blocked = true;
                return;
            }

            var normalized = Path.GetFullPath(path);
            if (!IsWithin(root, normalized) || !File.Exists(normalized))
            {
                AddRisk(DependencyRiskSeverity.Blocked, "local-path", normalized,
                    "Dependency_Risk_PathBlocked");
                blocked = true;
                return;
            }
            if (ContainsReparsePoint(root, normalized))
            {
                AddRisk(DependencyRiskSeverity.Blocked, "reparse-point", normalized,
                    "Dependency_Risk_ReparsePoint");
                blocked = true;
                return;
            }
            if (!visited.Add(normalized))
            {
                AddRisk(DependencyRiskSeverity.Blocked, "recursive-cycle", normalized,
                    "Dependency_Risk_RecursiveCycle");
                blocked = true;
                return;
            }
            if (visited.Count > MaximumFiles)
                throw new IOException("Too many nested requirements files.");

            var info = new FileInfo(normalized);
            if (info.Length > MaximumFileBytes || info.Length < 0 || totalBytes + info.Length > MaximumTotalBytes)
                throw new IOException("Requirements files exceed the allowed size.");
            totalBytes += info.Length;

            var bytes = await File.ReadAllBytesAsync(normalized, cancellationToken);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            fingerprints.Add(new RequirementsFileFingerprint(
                normalized, info.Length, info.LastWriteTimeUtc, hash));

            var directory = Path.GetDirectoryName(normalized)!;
            foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                if (TryOptionValue(line, "-r", "--requirement", out var nested) ||
                    TryOptionValue(line, "-c", "--constraint", out nested))
                {
                    if (IsRemote(nested))
                    {
                        AddRisk(DependencyRiskSeverity.Blocked, "remote-include", nested,
                            "Dependency_Risk_RemoteInclude");
                        blocked = true;
                        continue;
                    }
                    await VisitAsync(ResolveLocalPath(directory, nested), depth + 1);
                    continue;
                }

                if (TryOptionValue(line, "--trusted-host", "--trusted-host", out var trustedHost))
                {
                    AddRisk(DependencyRiskSeverity.Blocked, "trusted-host", trustedHost,
                        "Dependency_Risk_TrustedHost");
                    blocked = true;
                    continue;
                }

                if (TryOptionValue(line, "--index-url", "--index-url", out var index) ||
                    TryOptionValue(line, "--extra-index-url", "--extra-index-url", out index))
                {
                    AddExternalSourceRisk("package-index", index, requireHttps: true);
                    continue;
                }

                if (TryOptionValue(line, "-f", "--find-links", out var findLinks))
                {
                    if (IsRemote(findLinks)) AddExternalSourceRisk("find-links", findLinks, requireHttps: true);
                    else ValidateLocalSource(directory, findLinks, "find-links");
                    continue;
                }

                if (TryOptionValue(line, "-e", "--editable", out var editable))
                {
                    AddRisk(DependencyRiskSeverity.Warning, "editable", editable,
                        "Dependency_Risk_Editable");
                    requiresConfirmation = true;
                    if (editable.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
                        AddExternalSourceRisk("vcs", editable, requireHttps: true);
                    else
                        ValidateLocalSource(directory, editable, "editable");
                    continue;
                }

                if (line.StartsWith("git+", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains(" @ git+", StringComparison.OrdinalIgnoreCase))
                {
                    var source = ExtractSource(line, "git+");
                    AddExternalSourceRisk("vcs", source, requireHttps: true);
                }
                else if (line.Contains(" @ http", StringComparison.OrdinalIgnoreCase))
                {
                    var source = line[(line.IndexOf(" @ ", StringComparison.Ordinal) + 3)..].Trim();
                    AddExternalSourceRisk("direct-url", source, requireHttps: true);
                }
                else if (LooksLikeLocalPath(line))
                {
                    ValidateLocalSource(directory, line.Split(';', 2)[0].Trim(), "local-source");
                }
                else if (line.StartsWith('-') && !IsAllowedOption(line))
                {
                    AddRisk(DependencyRiskSeverity.Blocked, "unsupported-option", line,
                        "Dependency_Risk_UnsupportedOption");
                    blocked = true;
                }

                var match = PackageName().Match(line);
                if (match.Success)
                {
                    var name = NormalizePackageName(match.Groups[1].Value);
                    requested.TryAdd(name, DependencyTextSanitizer.Sanitize(line, 512));
                }
            }
        }

        void AddExternalSourceRisk(string kind, string source, bool requireHttps)
        {
            var uriText = source.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
                ? source[4..]
                : source;
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) ||
                (requireHttps && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                AddRisk(DependencyRiskSeverity.Blocked, kind, source,
                    "Dependency_Risk_InsecureSource");
                blocked = true;
                return;
            }
            AddRisk(DependencyRiskSeverity.Warning, kind, source,
                "Dependency_Risk_ExternalSource");
            requiresConfirmation = true;
        }

        void ValidateLocalSource(string directory, string source, string kind)
        {
            try
            {
                var local = ResolveLocalPath(directory, source);
                if (!IsWithin(root, local) || (!File.Exists(local) && !Directory.Exists(local)) ||
                    ContainsReparsePoint(root, local))
                {
                    AddRisk(DependencyRiskSeverity.Blocked, kind, source,
                        "Dependency_Risk_PathBlocked");
                    blocked = true;
                    return;
                }
                AddRisk(DependencyRiskSeverity.Warning, kind, local,
                    "Dependency_Risk_LocalSource");
                requiresConfirmation = true;
            }
            catch
            {
                AddRisk(DependencyRiskSeverity.Blocked, kind, source,
                    "Dependency_Risk_PathBlocked");
                blocked = true;
            }
        }

        void AddRisk(DependencyRiskSeverity severity, string kind, string source, string key)
        {
            var display = DependencyTextSanitizer.SanitizeSource(source);
            if (risks.Any(r => r.Severity == severity && r.Kind == kind && r.DisplaySource == display)) return;
            risks.Add(new DependencyRisk(severity, kind, display, key));
        }

        await VisitAsync(requirements, 0);
        var ordered = fingerprints.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var combined = new StringBuilder(normalizedScript).Append('\n').Append(normalizedInterpreter).Append('\n');
        foreach (var fingerprint in ordered)
            combined.Append(fingerprint.Path).Append('\n').Append(fingerprint.Length).Append('\n')
                .Append(fingerprint.LastWriteTimeUtc.UtcTicks).Append('\n').Append(fingerprint.Sha256).Append('\n');
        var combinedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(combined.ToString())));
        var snapshot = new RequirementsSnapshot(
            normalizedScript, normalizedInterpreter, Path.GetFullPath(requirements), combinedHash, ordered);
        return new RequirementsPolicyResult(true, blocked, requiresConfirmation, snapshot, risks, requested);
    }

    public async Task<bool> IsCurrentAsync(RequirementsSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        try
        {
            var current = await AnalyzeAsync(snapshot.ScriptPath, snapshot.InterpreterPath, cancellationToken);
            return current.Found && current.Snapshot is not null &&
                   string.Equals(current.Snapshot.Fingerprint, snapshot.Fingerprint, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryOptionValue(string line, string shortName, string longName, out string value)
    {
        foreach (var name in new[] { shortName, longName }.Distinct(StringComparer.Ordinal))
        {
            if (line.Equals(name, StringComparison.OrdinalIgnoreCase)) break;
            if (line.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = Unquote(line[(name.Length + 1)..].Trim());
                return value.Length != 0;
            }
            if (line.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
            {
                value = Unquote(line[(name.Length + 1)..].Trim());
                return value.Length != 0;
            }
        }
        value = string.Empty;
        return false;
    }

    private static string ResolveLocalPath(string directory, string source)
    {
        var value = Unquote(source.Trim());
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
            value = uri.LocalPath;
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(directory, value));
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private static bool IsRemote(string value) =>
        Uri.TryCreate(Unquote(value), UriKind.Absolute, out var uri) && !uri.IsFile;

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsReparsePoint(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var normalizedPath = Path.GetFullPath(path);
        if (!IsWithin(normalizedRoot, normalizedPath)) return true;
        var current = normalizedRoot;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        var relative = Path.GetRelativePath(normalizedRoot, normalizedPath);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "." or "" || segment == "..") continue;
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private static string ExtractSource(string line, string marker)
    {
        var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? line : line[index..].Split((char[]?)null, 2)[0];
    }

    private static bool LooksLikeLocalPath(string line)
    {
        var value = line.Split(';', 2)[0].Trim();
        return value.StartsWith('.') || value.StartsWith('/') || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
               (value.Length > 2 && char.IsAsciiLetter(value[0]) && value[1] == ':');
    }

    private static bool IsAllowedOption(string line) =>
        new[] { "--hash", "--pre", "--prefer-binary", "--no-binary", "--only-binary", "--require-hashes", "--use-feature", "--use-deprecated" }
            .Any(option => line.Equals(option, StringComparison.OrdinalIgnoreCase) ||
                           line.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase) ||
                           line.StartsWith(option + " ", StringComparison.OrdinalIgnoreCase));

    internal static string NormalizePackageName(string name) =>
        PackageSeparator().Replace(name.Trim(), "-").ToLowerInvariant();

    [GeneratedRegex(@"^([A-Za-z0-9][A-Za-z0-9._-]*)(?:\[[^\]]+\])?\s*(?:[<>=!~].*)?(?:\s*;.*)?$")]
    private static partial Regex PackageName();

    [GeneratedRegex(@"[-_.]+")]
    private static partial Regex PackageSeparator();
}
