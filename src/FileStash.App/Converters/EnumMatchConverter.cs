using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FileStash.App.Converters;

/// <summary>单选按钮与枚举的匹配：IsChecked = (当前值 == ConverterParameter)。</summary>
public sealed class EnumMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 只有"这一档被选中"才写回；取消选中的那一档不能把源属性抹成另一档
        if (value is not true || parameter is null)
        {
            return Binding.DoNothing;
        }

        // IsChecked 是 bool，源是枚举：把 ConverterParameter 那个成员名转成枚举本身。
        // 原来这里无条件返回 DoNothing，等于每次点击的写回都被丢掉 —— 分段控件看着会动，
        // 模型一直是旧值。
        var name = parameter.ToString();

        return targetType.IsEnum && name is not null
            ? Enum.Parse(targetType, name)
            : parameter;
    }
}

/// <summary>布尔 → 可见性。参数传 "Inverse" 时取反。</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;

        if (string.Equals(parameter as string, "Inverse", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}
