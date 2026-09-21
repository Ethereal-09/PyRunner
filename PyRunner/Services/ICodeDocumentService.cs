using PyRunner.Editor;

namespace PyRunner.Services;

public interface ICodeDocumentService
{
    Task<CodeDocument> LoadAsync(string scriptPath, CancellationToken cancellationToken = default);
    Task<bool> IsCurrentAsync(CodeDocumentFingerprint fingerprint, CancellationToken cancellationToken = default);
    Task<CodeDocumentSaveResult> SaveAsync(
        CodeDocument document,
        string text,
        bool overwriteExternalChanges,
        CancellationToken cancellationToken = default);
    Task<CodeDocumentSaveResult> SaveAsAsync(
        CodeDocument document,
        string targetPath,
        string text,
        CancellationToken cancellationToken = default);
}

public interface IRecoveryDraftService
{
    Task SaveAsync(RecoveryDraft draft, CancellationToken cancellationToken = default);
    Task<RecoveryDraft?> LoadAsync(string scriptPath, CancellationToken cancellationToken = default);
    void Delete(string scriptPath);
}

public interface IScriptTemplateService
{
    IReadOnlyList<ScriptTemplate> GetTemplates();
    ScriptTemplateCreateResult Create(string templateId, string targetDirectory, string fileName);
}

public interface IExternalEditorLauncher
{
    Task<bool> OpenAsync(string filePath);
}

public interface ICodeEditorInteraction
{
    Task<UnsavedChangesChoice> ConfirmUnsavedChangesAsync(CancellationToken cancellationToken = default);
    Task<ExternalConflictChoice> ResolveExternalConflictAsync(CancellationToken cancellationToken = default);
    Task<bool> ConfirmRestoreDraftAsync(DateTimeOffset savedAtUtc, CancellationToken cancellationToken = default);
    Task<string?> PickSaveAsPathAsync(string currentPath, CancellationToken cancellationToken = default);
}
