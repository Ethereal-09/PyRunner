using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PyRunner.Editor;

namespace PyRunner.Services;

public sealed class RecoveryDraftService : IRecoveryDraftService
{
    private const int MaximumDrafts = 20;
    private const long MaximumTotalBytes = 2L * 1024 * 1024;
    private readonly string _root;

    public RecoveryDraftService(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PyRunner", "Drafts");
    }

    public async Task SaveAsync(RecoveryDraft draft, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(draft);
        if (bytes.LongLength > CodeDocumentService.EditableSizeLimit)
            throw new IOException("Editor_Error_DraftTooLarge");
        var target = DraftPath(draft.ScriptPath);
        var temp = Path.Combine(_root, $"{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            File.Move(temp, target, overwrite: true);
            Prune();
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public async Task<RecoveryDraft?> LoadAsync(string scriptPath, CancellationToken cancellationToken = default)
    {
        var path = DraftPath(scriptPath);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<RecoveryDraft>(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    public void Delete(string scriptPath)
    {
        try { File.Delete(DraftPath(scriptPath)); } catch { }
    }

    private string DraftPath(string scriptPath)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(scriptPath).ToUpperInvariant())));
        return Path.Combine(_root, key + ".json");
    }

    private void Prune()
    {
        var files = new DirectoryInfo(_root).EnumerateFiles("*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc).ToList();
        long total = 0;
        for (var index = 0; index < files.Count; index++)
        {
            total += files[index].Length;
            if (index < MaximumDrafts && total <= MaximumTotalBytes) continue;
            try { files[index].Delete(); } catch { }
        }
    }
}
