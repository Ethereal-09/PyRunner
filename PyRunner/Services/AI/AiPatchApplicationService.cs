using System.Security.Cryptography;
using System.Text;
using PyRunner.Models;

namespace PyRunner.Services.AI;

public sealed class AiPatchApplicationService : IAiPatchApplicationService
{
    private readonly string _backupRoot;

    static AiPatchApplicationService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public AiPatchApplicationService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PyRunner", "AiBackups")) { }

    internal AiPatchApplicationService(string backupRoot) => _backupRoot = Path.GetFullPath(backupRoot);

    public async Task<AiPatchApplyResult> ApplyAsync(IReadOnlyList<AiPendingFileChange> changes,
        CancellationToken cancellationToken = default)
    {
        if (changes.Count is < 1 or > 8)
            return new(false, "AI_Error_InvalidPatch", null);
        var normalized = changes.Select(change => change with { TargetPath = Path.GetFullPath(change.TargetPath) }).ToArray();
        if (normalized.Select(change => change.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            return new(false, "AI_Error_InvalidPatch", null);

        var backupDirectory = Path.Combine(_backupRoot, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var backups = new List<(string Target, string Backup)>();
        var applied = new List<(string Target, string Backup)>();
        try
        {
            foreach (var change in normalized)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRegularPythonFile(change.TargetPath);
                var bytes = await File.ReadAllBytesAsync(change.TargetPath, cancellationToken);
                if (!FixedHashEquals(change.OriginalSha256, bytes))
                    return new(false, "AI_Error_SourceChanged", null);
            }

            Directory.CreateDirectory(backupDirectory);
            for (var index = 0; index < normalized.Length; index++)
            {
                var target = normalized[index].TargetPath;
                var backup = Path.Combine(backupDirectory, $"{index + 1:D2}-{Path.GetFileName(target)}.bak");
                File.Copy(target, backup, overwrite: false);
                backups.Add((target, backup));
            }

            foreach (var change in normalized)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRegularPythonFile(change.TargetPath);
                var current = await File.ReadAllBytesAsync(change.TargetPath, cancellationToken);
                if (!FixedHashEquals(change.OriginalSha256, current))
                    throw new IOException("AI_Error_SourceChanged");
                var temp = Path.Combine(Path.GetDirectoryName(change.TargetPath)!, $".pyrunner-ai-{Guid.NewGuid():N}.tmp");
                try
                {
                    var content = Encode(change);
                    await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                     64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await stream.WriteAsync(content, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                        stream.Flush(true);
                    }
                    var backup = backups.Single(item => item.Target.Equals(change.TargetPath, StringComparison.OrdinalIgnoreCase)).Backup;
                    File.Replace(temp, change.TargetPath, null, ignoreMetadataErrors: true);
                    applied.Add((change.TargetPath, backup));
                }
                finally { TryDelete(temp); }
            }
            return new(true, string.Empty, backupDirectory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RollBack(applied);
            return new(false, "AI_Error_Cancelled", backups.Count > 0 ? backupDirectory : null);
        }
        catch (IOException ex) when (ex.Message == "AI_Error_SourceChanged")
        {
            RollBack(applied);
            return new(false, "AI_Error_SourceChanged", backups.Count > 0 ? backupDirectory : null);
        }
        catch
        {
            RollBack(applied);
            return new(false, "AI_Error_ApplyFailed", backups.Count > 0 ? backupDirectory : null);
        }
    }

    private static byte[] Encode(AiPendingFileChange change)
    {
        var encoding = change.EncodingName.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            ? new UTF8Encoding(change.HasBom, true)
            : Encoding.GetEncoding(change.EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        var text = change.NewText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Replace("\n", change.NewLine, StringComparison.Ordinal);
        var body = encoding.GetBytes(text);
        var preamble = change.HasBom ? encoding.GetPreamble() : Array.Empty<byte>();
        return [.. preamble, .. body];
    }

    private static void ValidateRegularPythonFile(string path)
    {
        if (!path.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new IOException("AI_Error_InvalidPatch");
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0) continue;
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("AI_Error_InvalidPatch");
        }
    }

    private static bool FixedHashEquals(string expected, byte[] bytes)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), SHA256.HashData(bytes)); }
        catch { return false; }
    }

    private static void RollBack(IEnumerable<(string Target, string Backup)> applied)
    {
        foreach (var (target, backup) in applied.Reverse())
        {
            try
            {
                var temp = Path.Combine(Path.GetDirectoryName(target)!, $".pyrunner-ai-rollback-{Guid.NewGuid():N}.tmp");
                File.Copy(backup, temp, overwrite: false);
                File.Replace(temp, target, null, ignoreMetadataErrors: true);
                TryDelete(temp);
            }
            catch { }
        }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
