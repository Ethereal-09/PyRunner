namespace PyRunner.Services;

/// <summary>
/// 服务层业务校验异常（约定类型化）：
/// <see cref="LocalizationKey"/> 为本地化词条键（如 Error_PathDuplicate），
/// 由 ViewModel 捕获并经 ILocalizationService 翻译展示；
/// 数据层/IO 等非业务异常不做包装，由调用方按通用故障路径处理。
/// </summary>
public sealed class ValidationException : Exception
{
    /// <summary>本地化词条键（同时作为 Message，便于日志定位）。</summary>
    public string LocalizationKey { get; }

    public ValidationException(string localizationKey) : base(localizationKey)
    {
        LocalizationKey = localizationKey;
    }
}
