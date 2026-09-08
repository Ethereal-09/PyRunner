using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using PyRunner.Models;
using PyRunner.ViewModels;

namespace PyRunner.Views;

/// <summary>
/// TextDecorations 枚举值助手：Windows.UI.Text.TextDecorations 在 Microsoft.WinUI 与
/// Windows SDK 投影中同名双投影，源码中任何命名引用（含 using 别名）均触发 CS0433；
/// 改从 TextBlock.TextDecorations 属性实例取枚举类型（元数据解析不歧义），
/// 再以数值构造成员（None=0 / Strikethrough=2），类型身份与绑定目标属性严格一致。
/// </summary>
internal static class TextDecorationsValues
{
    public static readonly object None;
    public static readonly object Strikethrough;

    static TextDecorationsValues()
    {
        None = new TextBlock().TextDecorations;            // 默认 None
        Strikethrough = Enum.ToObject(None.GetType(), 2);  // Strikethrough = 2
    }
}

/// <summary>
/// 主题色刷解析助手（Phase C）：转换器无法在 XAML 里写 ThemeResource，
/// 改由运行时从 Application.Resources 按键取色，与 DarkTheme.xaml 保持单一真相源；
/// 资源未就绪时回退同值直写刷，保证不崩不白屏。
/// </summary>
internal static class ThemeBrushes
{
    private static ElementTheme _currentTheme = ElementTheme.Dark;

    public static ElementTheme CurrentTheme => _currentTheme;

    public static void SetTheme(ElementTheme theme) =>
        _currentTheme = theme == ElementTheme.Light ? ElementTheme.Light : ElementTheme.Dark;

    public static Brush Get(string key, uint fallbackArgb)
    {
        // Application.Resources 会按 Windows 系统主题解析 ThemeDictionary，而本应用允许
        // 窗口内独立切换主题。动态创建的 Brush 必须先按窗口主题取语义回退色，否则会
        // 出现“深色 XAML + 浅色转换器文字”的混合状态。
        var themed = ThemeFallback(key);
        if (themed is uint themedArgb)
            return CreateBrush(themedArgb);

        if (Application.Current?.Resources is { } resources && TryFindBrush(resources, key, out var brush))
            return brush;

        var fallback = _currentTheme == ElementTheme.Light ? LightFallback(key, fallbackArgb) : fallbackArgb;
        return CreateBrush(fallback);
    }

    private static SolidColorBrush CreateBrush(uint argb) =>
        new(Windows.UI.Color.FromArgb(
            (byte)(argb >> 24), (byte)(argb >> 16),
            (byte)(argb >> 8), (byte)argb));

    private static uint? ThemeFallback(string key) => _currentTheme == ElementTheme.Light
        ? key switch
        {
            "AccentBrush" => 0xFF2F9E70,
            "AccentLightBrush" => 0xFF27865E,
            "GhostButtonBrush" => 0xFFF6F8FA,
            "GhostButtonHoverBrush" => 0xFFEDF3F0,
            "NavigationSelectedBrush" => 0xFFDCEFE7,
            "SecondaryButtonBrush" => 0xFFFFFFFF,
            "SecondaryButtonHoverBrush" => 0xFFEDF3F0,
            "SecondaryButtonPressedBrush" => 0xFFDCEFE7,
            "SecondaryButtonBorderBrush" => 0xFFD8DEE5,
            "SecondaryButtonForegroundBrush" => 0xFF20262E,
            "PrimaryButtonForegroundBrush" => 0xFFFFFFFF,
            "ControlSurfaceBrush" => 0xFFF6F8FA,
            "DisabledButtonBrush" => 0xFFF0F2F4,
            "DisabledButtonForegroundBrush" => 0xFF8B959F,
            "BorderSubtleBrush" => 0xFFD8DEE5,
            "BorderHoverBrush" => 0xFFB8C2CC,
            "SelectedBorderBrush" => 0xFF2F9E70,
            "DangerSurfaceBrush" => 0x20C42B3A,
            "DangerHoverSurfaceBrush" => 0x2EC42B3A,
            "DangerPressedSurfaceBrush" => 0x40C42B3A,
            "StatusRunningSurfaceBrush" => 0x2ED89C24,
            "StatusSuccessSurfaceBrush" => 0x292F9E70,
            "StatusErrorSurfaceBrush" => 0x29C42B3A,
            "StatusStoppedSurfaceBrush" => 0xFFE9EEF2,
            "StatusRunningBrush" => 0xFF8A5B00,
            "StatusSuccessBrush" => 0xFF27865E,
            "StatusErrorBrush" => 0xFFB42332,
            "StatusStoppedBrush" => 0xFF5F6B78,
            "AccentHoverBrush" => 0xFF27865E,
            "AccentPressedBrush" => 0xFF217451,
            "TextPrimaryBrush" => 0xFF20262E,
            "TextBodyBrush" => 0xFF3F4A55,
            "TextSecondaryBrush" => 0xFF5F6B78,
            "TextMutedBrush" => 0xFF6C7884,
            "TextDisabledBrush" => 0xFF8B959F,
            "OnboardingCardBrush" => 0xFF252A2D,
            "OnboardingBorderBrush" => 0xFF30363A,
            "OnboardingAccentSurfaceBrush" => 0x3323684E,
            "OnboardingSecondaryTextBrush" => 0xFF9AA3A0,
            _ => null,
        }
        : key switch
        {
            "AccentBrush" => 0xFF35A875,
            "AccentLightBrush" => 0xFF67CEA0,
            "GhostButtonBrush" => 0xFF2B2F33,
            "GhostButtonHoverBrush" => 0xFF30363A,
            "NavigationSelectedBrush" => 0xFF30363A,
            "SecondaryButtonBrush" => 0xFF2B2F33,
            "SecondaryButtonHoverBrush" => 0xFF343A3F,
            "SecondaryButtonPressedBrush" => 0xFF202428,
            "SecondaryButtonBorderBrush" => 0xFF41484F,
            "SecondaryButtonForegroundBrush" => 0xFFF0F2F4,
            "PrimaryButtonForegroundBrush" => 0xFFFFFFFF,
            "ControlSurfaceBrush" => 0xFF2B2F33,
            "DisabledButtonBrush" => 0xFF24282C,
            "DisabledButtonForegroundBrush" => 0xFF7E8995,
            "BorderSubtleBrush" => 0xFF343A40,
            "BorderHoverBrush" => 0xFF495159,
            "SelectedBorderBrush" => 0xFF3DB982,
            "DangerSurfaceBrush" => 0x2EFF8F8F,
            "DangerHoverSurfaceBrush" => 0x42FF8F8F,
            "DangerPressedSurfaceBrush" => 0x5CFF8F8F,
            "StatusRunningSurfaceBrush" => 0x38E6C07B,
            "StatusSuccessSurfaceBrush" => 0x3335A875,
            "StatusErrorSurfaceBrush" => 0x33FF8F8F,
            "StatusStoppedSurfaceBrush" => 0xFF343A40,
            "StatusRunningBrush" => 0xFFFFD479,
            "StatusSuccessBrush" => 0xFF67CEA0,
            "StatusErrorBrush" => 0xFFFF8F8F,
            "StatusStoppedBrush" => 0xFFAAB2BC,
            "AccentHoverBrush" => 0xFF3DB982,
            "AccentPressedBrush" => 0xFF2B8E63,
            "TextPrimaryBrush" => 0xFFF0F2F4,
            "TextBodyBrush" => 0xFFD9DEE4,
            "TextSecondaryBrush" => 0xFFAAB2BC,
            "TextMutedBrush" => 0xFF8F9AA6,
            "TextDisabledBrush" => 0xFF697580,
            "OnboardingCardBrush" => 0xFF252A2D,
            "OnboardingBorderBrush" => 0xFF30363A,
            "OnboardingAccentSurfaceBrush" => 0x3323684E,
            "OnboardingSecondaryTextBrush" => 0xFF9AA3A0,
            _ => null,
        };

    private static bool TryFindBrush(ResourceDictionary dictionary, string key, out Brush brush)
    {
        var themeKey = _currentTheme == ElementTheme.Light ? "Light" : "Dark";
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themedValue) &&
            themedValue is ResourceDictionary themedDictionary &&
            TryFindBrushInDictionary(themedDictionary, key, out brush))
        {
            return true;
        }

        if (dictionary.TryGetValue(key, out var directValue) && directValue is Brush directBrush)
        {
            brush = directBrush;
            return true;
        }

        for (var i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (TryFindBrush(dictionary.MergedDictionaries[i], key, out brush))
                return true;
        }

        brush = null!;
        return false;
    }

    private static bool TryFindBrushInDictionary(ResourceDictionary dictionary, string key, out Brush brush)
    {
        if (dictionary.TryGetValue(key, out var value) && value is Brush found)
        {
            brush = found;
            return true;
        }

        for (var i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (TryFindBrushInDictionary(dictionary.MergedDictionaries[i], key, out brush))
                return true;
        }

        brush = null!;
        return false;
    }

    private static uint LightFallback(string key, uint original) => key switch
    {
        "AccentBrush" => 0xFF278D61,
        "GhostButtonBrush" => 0x0D17212A,
        "GhostButtonHoverBrush" => 0x1717212A,
        "ControlSurfaceBrush" => 0xFFE8EDF1,
        "DangerSurfaceBrush" => 0x20C42B3A,
        "StatusRunningSurfaceBrush" => 0x2ED89C24,
        "StatusSuccessSurfaceBrush" => 0x29278D61,
        "StatusErrorSurfaceBrush" => 0x29C42B3A,
        "StatusRunningBrush" => 0xFF8A5B00,
        "StatusSuccessBrush" => 0xFF167249,
        "StatusErrorBrush" => 0xFFB42332,
        "TextBodyBrush" => 0xFF394651,
        "TextMutedBrush" => 0xFF687681,
        "TextDisabledBrush" => 0xFF929CA5,
        _ => original,
    };
}

/// <summary>只用于展示的中间省略，保留路径开头与末尾文件名；原始值仍用于提示和业务。</summary>
internal static class PathDisplay
{
    public static string MiddleEllipsis(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || maxLength < 5 || value.Length <= maxLength) return value ?? string.Empty;
        var tailLength = Math.Max(1, (maxLength - 1) / 2);
        var headLength = maxLength - tailLength - 1;
        return value[..headLength] + "…" + value[^tailLength..];
    }
}

public sealed class MiddleEllipsisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var maxLength = parameter is string text && int.TryParse(text, out var parsed) ? parsed : 48;
        return PathDisplay.MiddleEllipsis(value?.ToString(), maxLength);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// bool → Visibility。parameter = "Invert" 时取反（true → Collapsed）。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string s && string.Equals(s, "Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// 运行状态徽章前景色（Phase C：色值收敛到 DarkTheme.xaml 状态色 Token）。
/// NotRun=TextMuted / Running=StatusRunning / Success=StatusSuccess / Failed·Killed=StatusError。
/// </summary>
public sealed class RunStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var status = value as RunStatus? ?? RunStatus.NotRun;
        return status switch
        {
            RunStatus.Running => ThemeBrushes.Get("StatusRunningBrush", 0xFFFFD479),
            RunStatus.Success => ThemeBrushes.Get("StatusSuccessBrush", 0xFF7FE0AE),
            RunStatus.Failed => ThemeBrushes.Get("StatusErrorBrush", 0xFFFF8F8F),
            RunStatus.Killed => ThemeBrushes.Get("StatusStoppedBrush", 0xFFAAB2BC),
            _ => ThemeBrushes.Get("TextMutedBrush", 0xFF8A949E),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>失效节点（路径不存在）前景色：置灰 TextDisabled，否则高对比正文色。</summary>
public sealed class InvalidNodeForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true
            ? ThemeBrushes.Get("TextDisabledBrush", 0xFF6B7A85)
            : ThemeBrushes.Get("TextPrimaryBrush", 0xFFF0F2F4);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>失效节点文字删除线：true → Strikethrough，否则无修饰。</summary>
public sealed class InvalidNodeTextDecorationsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? TextDecorationsValues.Strikethrough : TextDecorationsValues.None;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>bool 取反（设置弹窗「添加解释器」按钮在检测中禁用）。</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>有效性 → 状态色：true=StatusSuccess 绿，false=StatusError 红（设置弹窗路径状态）。</summary>
public sealed class ValidToStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true
            ? ThemeBrushes.Get("StatusSuccessBrush", 0xFF7FE0AE)
            : ThemeBrushes.Get("StatusErrorBrush", 0xFFFF8F8F);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// 文件树节点图标选择器（图标模板定义在 IconLibrary.xaml，Path 内联方式）。
/// </summary>
public sealed class FileTreeIconSelector : DataTemplateSelector
{
    public DataTemplate? FolderTemplate { get; set; }
    public DataTemplate? ScriptTemplate { get; set; }
    public DataTemplate? StarTemplate { get; set; }

    private DataTemplate? SelectIconTemplate(object item)
    {
        // RootNodes 模式下 item 为 TreeViewNode，需解包 Content 才是节点 VM
        if (item is Microsoft.UI.Xaml.Controls.TreeViewNode tvn) item = tvn.Content;
        if (item is not FileTreeNodeViewModel node) return FolderTemplate;
        return node.Kind switch
        {
            FileTreeNodeKind.Favorites => StarTemplate,
            FileTreeNodeKind.PyFile => ScriptTemplate,
            _ => FolderTemplate,
        };
    }

    // ContentControl 和 ItemsControl 在 WinUI 3 中分别走不同重载；两者都覆盖，
    // 否则 ContentControl 会回退为 ToString()，在 16px 图标槽里显示被裁掉的节点文字。
    protected override DataTemplate? SelectTemplateCore(object item) => SelectIconTemplate(item);

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectIconTemplate(item);
}
