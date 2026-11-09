using System.Windows;
using System.Windows.Media;

namespace FileStash.App.Controls;

/// <summary>
/// 主窗口预热。冷窗口（从没渲染过）显示要 141ms，热窗口 15ms，差快十倍——
/// 那 141ms 是在现场把整棵可视树建出来：控件模板展开、行实例化、主题字典解析。
/// 趁没人看的时候把它跑一遍，不用真的显示。
/// </summary>
internal static class WindowWarmUp
{
    public static void Warm(Window window)
    {
        // Window.Width/Height 的默认值是 NaN（XAML 里没写死尺寸、或用 SizeToContent 的窗口就是这样），
        // 而 UIElement.Measure 明确拒绝 NaN 并抛 InvalidOperationException。
        // 这段跑在每次启动的路径上，直接照抄进一个自适应尺寸的窗口就是启动即崩。
        var width = double.IsNaN(window.Width) ? window.RestoreBounds.Width : window.Width;
        var height = double.IsNaN(window.Height) ? window.RestoreBounds.Height : window.Height;

        if (double.IsNaN(width) || width <= 0)
        {
            width = window.MinWidth > 0 ? window.MinWidth : 800;
        }

        if (double.IsNaN(height) || height <= 0)
        {
            height = window.MinHeight > 0 ? window.MinHeight : 600;
        }

        var size = new Size(width, height);
        window.Measure(size);
        window.Arrange(new Rect(new Point(), size));
        window.UpdateLayout();
    }
}
