using PyRunner.Models;

namespace PyRunner.Services;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckLatestAsync(
        UpdateCheckCache? cachedResult,
        CancellationToken cancellationToken = default);

    UpdateCheckResult? RestoreCachedResult(UpdateCheckCache? cachedResult);
}

public interface IUpdateCacheStore
{
    UpdateCacheSnapshot Load();
    void SaveAttempt(DateTimeOffset attemptedAtUtc);
    void SaveSuccessfulResult(UpdateCheckCache result);
}

public interface IUpdateStateStore
{
    UpdateStateSnapshot Current { get; }
    event EventHandler<UpdateStateSnapshot>? Changed;
    void Initialize(UpdateCheckResult? successfulResult);
    void BeginCheck();
    void Complete(UpdateCheckResult result);
}
