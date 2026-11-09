using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FileStash.App.Controls;

/// <summary>Route the wheel to the scrollable area under the pointer, including its padding.</summary>
public static class WheelScroll
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(WheelScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetIsEnabled(UIElement element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(UIElement element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var control = (UIElement)sender;
        if ((bool)e.NewValue) control.PreviewMouseWheel += OnWheel;
        else control.PreviewMouseWheel -= OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || Keyboard.Modifiers != ModifierKeys.None) return;

        var area = (FrameworkElement)sender;
        var node = e.OriginalSource as DependencyObject;
        ScrollViewer? target = null;
        while (node != null)
        {
            if (node is ScrollViewer viewer && CanScroll(viewer, e.Delta))
            {
                target = viewer;
                break;
            }
            if (node == area) break;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        // If the pointer hit the list's border, its template's viewer is below the hit,
        // so the normal bubbling route never reaches it.
        if (target == null && area is ItemsControl)
            target = FindViewer(area);
        if (target == null || !CanScroll(target, e.Delta)) return;

        var wheel = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent
        };
        target.RaiseEvent(wheel);
        e.Handled = wheel.Handled;
    }

    private static bool CanScroll(ScrollViewer viewer, int delta) => delta < 0
        ? viewer.VerticalOffset < viewer.ScrollableHeight
        : viewer.VerticalOffset > 0;

    private static ScrollViewer? FindViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var viewer = child as ScrollViewer ?? FindViewer(child);
            if (viewer != null) return viewer;
        }
        return null;
    }
}
