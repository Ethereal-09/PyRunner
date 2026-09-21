using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PyRunner.Editor;

namespace PyRunner.Services;

public sealed partial class CodeDocumentService : ICodeDocumentService
{
    public const long EditableSizeLimit = 2L * 1024 * 1024;
    private const long AbsoluteSizeLimit = 8L * 1024 * 1024;
    private readonly IScriptService? _scripts;

    static CodeDocumentService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public CodeDocumentService(IScriptService? scripts = null) => _scripts = scripts;

    public async Task<CodeDocument> LoadAsync(string scriptPath, CancellationToken cancellationToken = default)
    {
        var path = ValidateExistingPythonFile(scriptPath);
        if (_scripts is not null && !_scripts.GetAll().Any(script =>
                string.Equals(Path.GetFullPath(script.FilePath), path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Editor_Error_NotRegistered");
        var info = new FileInfo(path);
        if (info.Length > AbsoluteSizeLimit)
            throw new IOException("Editor_Error_FileTooLarge");

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
            throw new IOException("Editor_Error_BinaryFile");

        var (encoding, hasBom, preambleLength) = DetectEncoding(bytes);
        string text;
        try { text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength); }
        catch (DecoderFallbackException) { throw new IOException("Editor_Error_EncodingUnsupported"); }

        var newLine = DetectNewLine(text);
        var readOnlyReason = string.Empty;
        if (info.Length > EditableSizeLimit) readOnlyReason = "Editor_ReadOnly_LargeFile";
        else if (info.IsReadOnly) readOnlyReason = "Editor_ReadOnly_Permission";

        return new CodeDocument(
            NormalizeNewLines(text),
            encoding.WebName,
            hasBom,
            newLine,
            readOnlyReason.Length != 0,
            readOnlyReason,
            Fingerprint(path, bytes, info));
    }

    public async Task<bool> IsCurrentAsync(CodeDocumentFingerprint fingerprint, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = ValidateExistingPythonFile(fingerprint.Path);
            var info = new FileInfo(path);
            if (info.Length != fingerprint.Length || info.LastWriteTimeUtc != fingerprint.LastWriteTimeUtc.UtcDateTime)
                return false;
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(fingerprint.Sha256), SHA256.HashData(bytes));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public async Task<CodeDocumentSaveResult> SaveAsync(
        CodeDocument document,
        string text,
        bool overwriteExternalChanges,
        CancellationToken cancellationToken = default)
    {
        if (document.IsReadOnly)
            return new(CodeDocumentSaveStatus.ReadOnly, null, document.ReadOnlyReason);
        if (!overwriteExternalChanges && !await IsCurrentAsync(document.Fingerprint, cancellationToken))
            return new(CodeDocumentSaveStatus.Conflict, null, "Editor_Error_ExternalChange");
        return await SaveCoreAsync(document, document.Fingerprint.Path, text, replaceExisting: true,
            overwriteExternalChanges, cancellationToken);
    }

    public Task<CodeDocumentSaveResult> SaveAsAsync(
        CodeDocument document,
        string targetPath,
        string text,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(document, ValidateNewPythonPath(targetPath), text, replaceExisting: false,
            overwriteExternalChanges: false, cancellationToken);

    private async Task<CodeDocumentSaveResult> SaveCoreAsync(
        CodeDocument document,
        string targetPath,
        string text,
        bool replaceExisting,
        bool overwriteExternalChanges,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath)!;
        var tempPath = Path.Combine(directory, $".pyrunner-{Guid.NewGuid():N}.tmp");
        try
        {
            var encoding = CreateEncoding(document.EncodingName, document.HasBom);
            var normalized = NormalizeNewLines(text).Replace("\n", document.NewLine, StringComparison.Ordinal);
            var content = encoding.GetBytes(normalized);
            if (content.LongLength + (document.HasBom ? encoding.GetPreamble().Length : 0) > EditableSizeLimit)
                return new(CodeDocumentSaveStatus.Failed, null, "Editor_Error_FileTooLarge");
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                if (document.HasBom)
                {
                    var preamble = encoding.GetPreamble();
                    await stream.WriteAsync(preamble, cancellationToken);
                }
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (replaceExisting)
            {
                if (!overwriteExternalChanges && !await IsCurrentAsync(document.Fingerprint, cancellationToken))
                    return new(CodeDocumentSaveStatus.Conflict, null, "Editor_Error_ExternalChange");
                File.Replace(tempPath, targetPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, targetPath, overwrite: false);
            }

            var saved = await LoadAsync(targetPath, cancellationToken);
            return new(CodeDocumentSaveStatus.Saved, saved, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (IOException) when (!replaceExisting && File.Exists(targetPath))
        {
            return new(CodeDocumentSaveStatus.Conflict, null, "Template_Error_AlreadyExists");
        }
        catch
        {
            return new(CodeDocumentSaveStatus.Failed, null, "Editor_Error_SaveFailed");
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { }
        }
    }

    private static CodeDocumentFingerprint Fingerprint(string path, byte[] bytes, FileInfo info) =>
        new(path, bytes.LongLength, info.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(bytes)));

    private static (Encoding Encoding, bool HasBom, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
            return (new UTF8Encoding(false, true), true, Encoding.UTF8.GetPreamble().Length);

        var probe = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512));
        var declarationProbe = string.Join("\n", probe.Split(['\r', '\n'], StringSplitOptions.None).Take(2));
        var declaration = CodingDeclaration().Match(declarationProbe);
        if (declaration.Success)
        {
            var encoding = CreateEncoding(declaration.Groups[1].Value, false);
            return (encoding, false, 0);
        }

        return (new UTF8Encoding(false, true), false, 0);
    }

    private static Encoding CreateEncoding(string name, bool emitBom)
    {
        if (name.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("utf8", StringComparison.OrdinalIgnoreCase))
            return new UTF8Encoding(emitBom, true);
        var encoding = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        if (encoding.CodePage == 65000)
            throw new IOException("Editor_Error_EncodingUnsupported");
        return encoding;
    }

    private static string DetectNewLine(string text)
    {
        var crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
        var lf = text.IndexOf('\n');
        var cr = text.IndexOf('\r');
        if (crlf >= 0 && (lf < 0 || crlf <= lf) && (cr < 0 || crlf <= cr)) return "\r\n";
        if (lf >= 0 && (cr < 0 || lf < cr)) return "\n";
        return cr >= 0 ? "\r" : "\r\n";
    }

    private static string NormalizeNewLines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string ValidateExistingPythonFile(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (!normalized.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || !File.Exists(normalized))
            throw new FileNotFoundException("Editor_Error_FileUnavailable", normalized);
        RejectReparsePoints(normalized);
        var attributes = File.GetAttributes(normalized);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Editor_Error_NotRegularFile");
        return normalized;
    }

    private static string ValidateNewPythonPath(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (!normalized.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Editor_Error_NotPython");
        var directory = Path.GetDirectoryName(normalized)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        RejectReparsePoints(directory);
        return normalized;
    }

    private static void RejectReparsePoints(string path)
    {
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var segment in path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0) continue;
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Editor_Error_ReparsePoint");
        }
    }

    [GeneratedRegex(@"(?im)^[ \t\f]*#.*?coding[:=][ \t]*([-_.a-zA-Z0-9]+)")]
    private static partial Regex CodingDeclaration();
}
