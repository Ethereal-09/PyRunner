using System.Text.RegularExpressions;
using PyRunner.Models;

namespace PyRunner.Services.AI;

public sealed partial class SecretScanService : ISecretScanService
{
    private const int MaximumFindings = 50;

    public IReadOnlyList<AiSecretFinding> Scan(string text)
    {
        var findings = new List<AiSecretFinding>();
        Add(ApiKeyRegex(), "credential", text, findings);
        Add(PrivateKeyRegex(), "private-key", text, findings);
        Add(ConnectionStringRegex(), "connection-string", text, findings);
        Add(CredentialUrlRegex(), "credential-url", text, findings);
        return findings.OrderBy(f => f.Start).Take(MaximumFindings).ToArray();
    }

    public string CreateMaskedPreview(string text, IReadOnlyList<AiSecretFinding> findings)
    {
        var value = text.Length <= 2000 ? text : text[..2000] + "\n…";
        foreach (var finding in findings.Where(f => f.Start < value.Length).OrderByDescending(f => f.Start))
        {
            var length = Math.Min(finding.Length, value.Length - finding.Start);
            value = value.Remove(finding.Start, length).Insert(finding.Start, $"[{finding.Kind} REDACTED]");
        }
        return value;
    }

    private static void Add(Regex regex, string kind, string text, List<AiSecretFinding> findings)
    {
        foreach (Match match in regex.Matches(text))
        {
            var line = 1 + text.AsSpan(0, match.Index).Count('\n');
            findings.Add(new AiSecretFinding(line, kind, match.Index, match.Length));
            if (findings.Count >= MaximumFindings) return;
        }
    }

    [GeneratedRegex(@"(?ix)\b(?:api[_-]?key|token|password|secret)\s*[:=]\s*['""]?[^\s'""]{8,}")]
    private static partial Regex ApiKeyRegex();
    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyRegex();
    [GeneratedRegex(@"(?ix)\b(?:Server|Data Source|Host)\s*=.+?(?:Password|Pwd)\s*=.+?(?:;|$)")]
    private static partial Regex ConnectionStringRegex();
    [GeneratedRegex(@"https?://[^\s/@:]+:[^\s/@]+@[^\s/]+", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialUrlRegex();
}
