using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FileStash.App.Converters;

/// <summary>
/// 将 null/非 null 转换为 Visibility。
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isNull = value is null;
        var invert = Invert || (parameter is string paramStr && string.Equals(paramStr, "Inverse", StringComparison.OrdinalIgnoreCase));

        if (invert)
        {
            return isNull ? Visibility.Visible : Visibility.Collapsed;
        }

        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
