namespace PyRunner.Models;

public enum UpdateCheckStatus
{
    NotChecked,
    Latest,
    UpdateAvailable,
    NoPublishedRelease,
    RateLimited,
    Timeout,
    Offline,
    InvalidReleaseData,
    Failed,
}

public enum UpdateCheckMode
{
    Automatic,
    Manual,
}

/// <summary>一次更新检查的非敏感结果。ReleaseNotes 始终是受限长度的纯文本。</summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? Version = null,
    DateTimeOffset? PublishedAt = null,
    string? ReleaseNotes = null,
    Uri? ReleasePageUri = null,
    string? ETag = null,
    DateTimeOffset? RateLimitResetAt = null,
    bool FromCache = false,
    DateTimeOffset? CheckedAtUtc = null)
{
    public bool IsSuccessful => Status is UpdateCheckStatus.Latest or UpdateCheckStatus.UpdateAvailable;
}

/// <summary>持久化的最后一次成功响应；失败检查不得覆盖本对象。</summary>
public sealed record UpdateCheckCache(
    string Version,
    DateTimeOffset PublishedAt,
    string ReleaseNotes,
    string ReleasePageUrl,
    string? ETag,
    DateTimeOffset CheckedAtUtc);

public sealed record UpdateCacheSnapshot(
    DateTimeOffset? LastAttemptAtUtc,
    UpdateCheckCache? LastSuccessfulResult);

public sealed record UpdateStateSnapshot(
    bool IsChecking,
    UpdateCheckResult? LastAttemptResult,
    UpdateCheckResult? LastSuccessfulResult);
