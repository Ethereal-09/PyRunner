using CommunityToolkit.Mvvm.ComponentModel;

namespace PyRunner.Services;

/// <summary>
/// 本地化服务契约：可观察包装器，x:Bind 索引器按 key 取值；
/// 切换语言触发 LanguageChanged 与索引器整体刷新（热切换全量生效为 Phase E）。
/// </summary>
public interface ILocalizationService
{
    /// <summary>当前语言代码（zh-CN / en-US）。</summary>
    string CurrentLanguage { get; }

    /// <summary>按 key 取当前语言文案；缺失时回退 en-US，再缺失返回 key 本身。</summary>
    string this[string key] { get; }

    /// <summary>切换语言：更新内存状态并写回 settings.json。</summary>
    void SetLanguage(string languageCode);

    /// <summary>语言切换后触发（UI 层可借此刷新非索引器绑定）。</summary>
    event Action? LanguageChanged;
}

/// <summary>
/// 本地化服务实现：启动时扫描程序集嵌入资源（GetManifestResourceNames），
/// 匹配 PyRunner.Resources.{文化名}.Strings.resw 前缀/后缀动态建表（无文化名段 = 默认 en-US），
/// 新增语言只需添加 resw + csproj 嵌入条目，无需改本类。
/// 语言默认按 settings.json（首次为 CurrentUICulture）。
/// 继承 ObservableObject：语言切换时对 "Item[]" 触发 PropertyChanged，
/// 使 x:Bind L["Key"] 绑定自动刷新。
/// </summary>
public sealed class LocalizationService : ObservableObject, ILocalizationService
{
    /// <summary>嵌入资源命名约定：PyRunner.Resources.[{文化名}.]Strings.resw。</summary>
    private const string ResourcePrefix = "PyRunner.Resources.";
    private const string ResourceSuffix = ".Strings.resw";

    /// <summary>默认语言（无文化名段的 Strings.resw），也是回退语言。</summary>
    private const string DefaultLanguage = "en-US";

    private readonly ISettingsService _settingsService;
    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);

    public event Action? LanguageChanged;

    public LocalizationService(ISettingsService settingsService)
    {
        _settingsService = settingsService;

        DiscoverAndLoadTables();

        CurrentLanguage = NormalizeLanguage(_settingsService.Current.Language);
    }

    /// <summary>扫描嵌入资源动态发现语言表，消除硬编码语言清单。</summary>
    private void DiscoverAndLoadTables()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            if (!resourceName.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase)) continue;

            // 资源名 = PyRunner.Resources.Strings.resw（默认）或 PyRunner.Resources.{文化名}.Strings.resw：
            // 先剥离前缀与 "Strings.resw" 主体，剩余的文化名段再去掉尾部 '.'
            var remainder = resourceName.Substring(ResourcePrefix.Length);
            remainder = remainder[..^"Strings.resw".Length].TrimEnd('.');
            var languageCode = remainder.Length == 0 ? DefaultLanguage : remainder;

            LoadTable(resourceName, languageCode);
        }

        WarnOnUnderscoredResourceNames();

        if (_tables.Count == 0)
        {
#if DEBUG
            DebugLog.WriteLine("Localization: 未发现任何嵌入语言表（检查 csproj EmbeddedResource 配置）");
#endif
        }
    }

    /// <summary>DEBUG 守卫：嵌入资源名含下划线文化段（如 zh_CN）通常意味着 csproj 未用
    /// LogicalName 固定文化名（MSBuild 默认把目录名中的 '-' 转成 '_'），导致发现逻辑
    /// 将其注册为非预期文化键、实际语言静默回退 en-US。Release 下调用点被编译器移除。</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void WarnOnUnderscoredResourceNames()
    {
#if DEBUG
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.Contains('_'))
            {
                DebugLog.WriteLine(
                    $"Localization: 嵌入资源名含下划线文化段（{name}），疑似漏配 csproj LogicalName（如 PyRunner.Resources.zh-CN.Strings.resw），可能导致静默回退 en-US");
            }
        }
#endif
    }

    public string CurrentLanguage { get; private set; }

    public string this[string key]
    {
        get
        {
            if (_tables.TryGetValue(CurrentLanguage, out var table) &&
                table.TryGetValue(key, out var value))
                return value;

            if (_tables.TryGetValue(DefaultLanguage, out var fallback) &&
                fallback.TryGetValue(key, out var fallbackValue))
                return fallbackValue;

            return key; // 词条缺失时暴露 key，便于开发期发现
        }
    }

    public void SetLanguage(string languageCode)
    {
        var normalized = NormalizeLanguage(languageCode);
        if (string.Equals(normalized, CurrentLanguage, StringComparison.OrdinalIgnoreCase))
            return;

        CurrentLanguage = normalized;

        _settingsService.Update(settings => settings.Language = normalized);

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }

    /// <summary>语言代码归一：按已发现的语言表匹配（精确 → 前缀，如 zh → zh-CN），
    /// 无匹配时回退默认语言。纯查找实现，不含递归路径。</summary>
    private string NormalizeLanguage(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            foreach (var available in _tables.Keys)
                if (string.Equals(available, code, StringComparison.OrdinalIgnoreCase))
                    return available;

            foreach (var available in _tables.Keys)
                if (available.Length > 0 && available.StartsWith(code, StringComparison.OrdinalIgnoreCase))
                    return available;
        }

        return _tables.ContainsKey(DefaultLanguage)
            ? DefaultLanguage
            : (_tables.Keys.FirstOrDefault() ?? DefaultLanguage);
    }

    /// <summary>解析嵌入的 .resw（XML）为 key→value 字典。</summary>
    private void LoadTable(string resourceName, string languageCode)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
#if DEBUG
                DebugLog.WriteLine($"Localization: 嵌入资源缺失 {resourceName}");
#endif
                _tables[languageCode] = table;
                return;
            }

            var document = System.Xml.Linq.XDocument.Load(stream);
            foreach (var data in document.Root?.Elements("data") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
            {
                var name = data.Attribute("name")?.Value;
                var value = data.Element("value")?.Value;
                if (!string.IsNullOrEmpty(name) && value != null)
                    table[name] = value;
            }
        }
        catch (Exception ex)
        {
            DebugWriteError($"Localization: 解析 {resourceName} 失败", ex);
        }

        _tables[languageCode] = table;
    }

    /// <summary>DEBUG 日志辅助：Release 下调用点被编译器移除，避免 CS0168。</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugWriteError(string context, Exception ex)
    {
#if DEBUG
        DebugLog.WriteLine($"{context}（{ex.Message}）");
#endif
    }
}
