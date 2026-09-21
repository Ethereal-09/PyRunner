namespace PyRunner.Models;

public enum AiProviderKind { OpenAI, OpenAiCompatible }
public enum AiOperation { Explain, Refactor, Optimize, Diagnose, GenerateScript }
public enum AiPermissionMode { ReadOnlyAnalysis, GenerateChanges }
public enum AiMessageRole { User, Assistant, System }
public enum AiMessageKind { Text, AnalysisStatus, Error, Cancelled, Proposal }
public enum AiRequestState { Idle, AwaitingConfirmation, Streaming, Reviewable, Completed, Cancelled, Failed }
public enum AiErrorKind { MissingCredential, InvalidConfiguration, Unauthorized, RateLimited, Server, Network, Timeout, Cancelled, ResponseTooLarge, InvalidResponse }

public sealed record AiConfiguration(
    AiProviderKind Provider,
    Uri Endpoint,
    string Model,
    int TimeoutSeconds = 90,
    bool AllowInsecureLoopback = false);

public sealed record AiCredentialKey(AiProviderKind Provider, string Host)
{
    public string Resource => $"PyRunner.AI/{Provider}/{Host.ToLowerInvariant()}";
}

public sealed record AiEditorContext(
    string Text,
    string DisplayName,
    bool IsSelection,
    int SelectionStart,
    int SelectionEnd,
    string BufferSha256,
    string PythonVersion = "Unknown",
    string? FilePath = null);

public sealed record AiSecretFinding(int Line, string Kind, int Start, int Length);

public sealed record AiSendPreview(
    string Host,
    string Model,
    string Scope,
    int CharacterCount,
    int EstimatedTokens,
    IReadOnlyList<AiSecretFinding> Findings);

public sealed record AiProviderRequest(
    AiOperation Operation,
    string Context,
    string? UserGoal,
    string PythonVersion,
    bool RequireStructuredCode,
    IReadOnlyList<AiConversationTurn>? History = null,
    AiPermissionMode Mode = AiPermissionMode.ReadOnlyAnalysis);

public sealed record AiConversationTurn(string Role, string Content);

public sealed record AiStreamEvent(string Delta, string? RequestId = null, int? InputTokens = null, int? OutputTokens = null);

public sealed record AiProviderResult(string Text, string? RequestId, int? InputTokens, int? OutputTokens);

public sealed record AiFileChange(string FileName, string Code, string Summary);

public sealed record AiCodeCandidate(
    string Summary,
    string Code,
    string Notes,
    IReadOnlyList<AiFileChange>? Files = null)
{
    public IReadOnlyList<AiFileChange> EffectiveFiles =>
        Files is { Count: > 0 } ? Files : [new AiFileChange(string.Empty, Code, Summary)];
}

public sealed record AiContextFile(
    string FullPath,
    string DisplayName,
    string Content,
    long SizeBytes,
    string Sha256,
    string EncodingName,
    bool HasBom,
    string NewLine,
    bool IsPrimary = false);

public sealed record AiStoredMessage(
    string Id,
    AiMessageRole Role,
    AiMessageKind Kind,
    string Content,
    DateTimeOffset CreatedAtUtc,
    string? RequestId = null);

public sealed record AiConversationSnapshot(
    string Id,
    string Title,
    AiPermissionMode Mode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<AiStoredMessage> Messages,
    IReadOnlyList<string> ContextPaths);

public sealed record AiConversationSummary(
    string Id,
    string Title,
    AiPermissionMode Mode,
    DateTimeOffset UpdatedAtUtc,
    int MessageCount);

public sealed record AiPendingFileChange(
    string TargetPath,
    string DisplayName,
    string OriginalSha256,
    string OriginalText,
    string NewText,
    string EncodingName,
    bool HasBom,
    string NewLine,
    string Summary);

public sealed record AiPatchProposal(
    string Summary,
    string Notes,
    IReadOnlyList<AiPendingFileChange> Changes,
    int AddedLines,
    int DeletedLines,
    string Diff);

public sealed record AiPatchApplyResult(bool Succeeded, string ErrorKey, string? BackupDirectory);

public sealed class AiProviderException : Exception
{
    public AiErrorKind Kind { get; }
    public string? RequestId { get; }
    public TimeSpan? RetryAfter { get; }

    public AiProviderException(AiErrorKind kind, string message, string? requestId = null,
        TimeSpan? retryAfter = null, Exception? innerException = null) : base(message, innerException)
    {
        Kind = kind;
        RequestId = requestId;
        RetryAfter = retryAfter;
    }
}
