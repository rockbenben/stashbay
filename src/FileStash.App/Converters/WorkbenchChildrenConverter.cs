using System.Globalization;
using System.Windows.Data;
using FileStash.Core.Models;

namespace FileStash.App.Converters;

/// <summary>
/// 列表档与单文件不生成子项：入参是（行的 Children，当前档位，可选的 IsFolder）。
/// TreeViewItem 的 HasItems 随之为假，模板里那条触发器就把展开箭头藏了，
/// 不需要为"平铺"再复制一份行模板。
/// </summary>
public sealed class WorkbenchChildrenConverter : IMultiValueConverter
{
    public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if ((values.Length > 1 && values[1] is WorkbenchViewMode.List)
            || (values.Length > 2 && values[2] is false))
        {
            return Array.Empty<object>();
        }

        return values.Length > 0 && values[0] is not null ? values[0] : Array.Empty<object>();
    }

    public object?[]? ConvertBack(object? value, Type[]? targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
