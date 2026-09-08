using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>复用现有 settings.json 保存检查时间、ETag 和最后一次成功结果。</summary>
public sealed class SettingsUpdateCacheStore : IUpdateCacheStore
{
    private readonly ISettingsService _settings;

    public SettingsUpdateCacheStore(ISettingsService settings) => _settings = settings;

    public UpdateCacheSnapshot Load()
    {
        var snapshot = _settings.Current;
        return new(snapshot.LastUpdateCheckAttemptUtc, snapshot.LastSuccessfulUpdateCheck);
    }

    public void SaveAttempt(DateTimeOffset attemptedAtUtc)
    {
        _settings.Update(settings => settings.LastUpdateCheckAttemptUtc = attemptedAtUtc);
    }

    public void SaveSuccessfulResult(UpdateCheckCache result)
    {
        _settings.Update(settings => settings.LastSuccessfulUpdateCheck = result);
    }
}
