using System.Windows;

namespace FileStash.App.Controls;

/// <summary>Oldest notice stays at the bottom; overflow waits for a free slot.</summary>
public static class ToastStackLayout
{
    public const double Margin = 16;
    public const double Gap = 8;

    public static IReadOnlyList<Rect> Arrange(Rect workArea, IReadOnlyList<Size> sizes)
    {
        var result = new List<Rect>();
        var bottom = workArea.Bottom - Margin;
        var top = workArea.Top + Margin;
        var width = Math.Max(1, workArea.Width - 2 * Margin);
        var height = Math.Max(1, workArea.Height - 2 * Margin);
        foreach (var size in sizes)
        {
            var h = Math.Min(size.Height, height);
            var w = Math.Min(size.Width, width);
            if (h <= 0 || w <= 0) throw new ArgumentOutOfRangeException(nameof(sizes));
            if (bottom - h < top) break;
            result.Add(new Rect(workArea.Right - Margin - w, bottom - h, w, h));
            bottom -= h + Gap;
        }
        return result;
    }
}
