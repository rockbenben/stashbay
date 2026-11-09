using System.Globalization;
using System.Windows.Data;
using FileStash.App.ViewModels;

namespace FileStash.App.Converters;

/// <summary>
/// 把字节数按界面同一套规则格式化（预览表里以前直接绑 <c>long</c>，
/// 于是屏上出现"6291456"这种没人会读的数，而主界面同一列写的是"6 MB"）。
/// </summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long bytes ? ItemViewModel.FormatSize(bytes) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
