using PyRunner.Models;

namespace PyRunner.Services.AI;

public interface IAiCredentialStore
{
    bool HasCredential(AiCredentialKey key);
    void Save(AiCredentialKey key, string secret);
    string? Retrieve(AiCredentialKey key);
    void Delete(AiCredentialKey key);
}

public interface IAiProviderClient
{
    Task<AiProviderResult> StreamAsync(AiConfiguration configuration, string apiKey,
        AiProviderRequest request, IProgress<AiStreamEvent>? progress, CancellationToken cancellationToken);
}

public interface IAiContextBuilder
{
    AiSendPreview BuildPreview(AiConfiguration configuration, AiEditorContext context, string? userGoal = null);
    AiProviderRequest BuildRequest(AiOperation operation, AiEditorContext context, string? userGoal);
    AiProviderRequest BuildConversationRequest(AiPermissionMode mode, IReadOnlyList<AiContextFile> files,
        IReadOnlyList<AiConversationTurn> history, string prompt);
}

public interface ISecretScanService
{
    IReadOnlyList<AiSecretFinding> Scan(string text);
    string CreateMaskedPreview(string text, IReadOnlyList<AiSecretFinding> findings);
}

public interface IAiChangeReviewService
{
    AiCodeCandidate? ParseCandidate(string responseText);
    bool IsOriginalCurrent(AiEditorContext original, AiEditorContext current);
    string CreateLineDiff(string original, string candidate);
}

public interface IAiInteractionService
{
    Task<bool> ConfirmSendAsync(AiSendPreview preview, string maskedPreview, CancellationToken cancellationToken);
    Task<bool> ConfirmApplyAsync(AiCodeCandidate candidate, string localDiff, CancellationToken cancellationToken);
    Task ShowDiffAsync(string localDiff, CancellationToken cancellationToken);
    Task<bool> ConfirmDeleteConversationAsync(string title, CancellationToken cancellationToken);
}

public interface IAiEditorBridge
{
    string? CurrentFilePath { get; }
    bool HasUnsavedChanges { get; }
    Task<AiEditorContext?> RequestAiContextAsync(CancellationToken cancellationToken = default);
    Task<bool> ApplyAiCandidateAsync(AiEditorContext original, string candidate, CancellationToken cancellationToken = default);
    Task<bool> PrepareToSwitchAsync(CancellationToken cancellationToken = default);
    void OpenGeneratedDraft(string code);
    Task ReloadFromDiskAsync(CancellationToken cancellationToken = default);
}

public interface IAiConversationStore
{
    IReadOnlyList<AiConversationSummary> List();
    AiConversationSnapshot? Load(string id);
    void Save(AiConversationSnapshot conversation);
    void Delete(string id);
}

public interface IAiFileContextService
{
    Task<AiContextFile> LoadAsync(string path, bool isPrimary, CancellationToken cancellationToken = default);
}

public interface IAiPatchApplicationService
{
    Task<AiPatchApplyResult> ApplyAsync(IReadOnlyList<AiPendingFileChange> changes,
        CancellationToken cancellationToken = default);
}
