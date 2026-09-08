using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace PyRunner.Views;

/// <summary>
/// 字符串非空 → Visible，空 → Collapsed。用于字段级红字错误的显隐。
/// </summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
