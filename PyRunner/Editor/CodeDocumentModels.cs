namespace PyRunner.Editor;

public sealed record CodeDocumentFingerprint(
    string Path,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256);

public sealed record CodeDocument(
    string Text,
    string EncodingName,
    bool HasBom,
    string NewLine,
    bool IsReadOnly,
    string ReadOnlyReason,
    CodeDocumentFingerprint Fingerprint);

public enum CodeDocumentSaveStatus
{
    Saved,
    Conflict,
    ReadOnly,
    Failed,
}

public sealed record CodeDocumentSaveResult(
    CodeDocumentSaveStatus Status,
    CodeDocument? Document,
    string ErrorKey);

public sealed record RecoveryDraft(
    string ScriptPath,
    string Text,
    DateTimeOffset SavedAtUtc,
    string BaseSha256);

public sealed record ScriptTemplate(
    string Id,
    string NameKey,
    string DescriptionKey,
    string Version,
    string Content);

public sealed record ScriptTemplateCreateResult(bool Succeeded, string? FilePath, string ErrorKey);

public enum UnsavedChangesChoice { Save, Discard, Cancel }
public enum ExternalConflictChoice { Reload, SaveAs, Overwrite, Cancel }
