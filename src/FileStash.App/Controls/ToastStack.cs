using System.Windows;
using System.Windows.Threading;
using FileStash.App.Views;

namespace FileStash.App.Controls;

/// <summary>One bounded notification queue per monitor, owned by its UI dispatcher.</summary>
internal sealed class ToastStack
{
    [ThreadStatic] private static ToastStack? _current;
    public static ToastStack Current => _current ??= new ToastStack();
    private readonly List<ResultToast> _windows = [];
    private bool _layoutScheduled;
    private DispatcherTimer? _settleTimer;

    public void Add(ResultToast window)
    {
        if (!_windows.Contains(window)) _windows.Add(window);
        Refresh();
    }

    public void Remove(ResultToast window)
    {
        _windows.Remove(window);
        if (_windows.Count == 0) _settleTimer?.Stop();
        else Refresh();
    }

    public void Refresh()
    {
        if (_layoutScheduled || _windows.Count == 0 || App.IsShuttingDown) return;
        var dispatcher = _windows[0].Dispatcher;
        if (dispatcher.HasShutdownStarted) return;
        _layoutScheduled = true;
        dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _layoutScheduled = false;
            if (!App.IsShuttingDown) Reflow();
        }));
    }

    private void Reflow()
    {
        // Snapshot the groups: Show/Hide and size changes can schedule another layout.
        foreach (var group in _windows.ToArray().GroupBy(WindowEffects.GetWindowWorkArea))
        {
            var windows = group.ToArray();
            var area = group.Key;
            foreach (var window in windows)
            {
                window.MaxHeight = Math.Max(window.MinHeight, Math.Min(392, area.Height - 2 * ToastStackLayout.Margin));
                window.Width = Math.Min(380, Math.Max(1, area.Width - 2 * ToastStackLayout.Margin));
            }
            var positions = ToastStackLayout.Arrange(area, windows.Select(window => new Size(
                window.ActualWidth > 0 ? window.ActualWidth : window.Width,
                window.ActualHeight > 0 ? window.ActualHeight : 132)).ToArray());

            var occupied = new Dictionary<ResultToast, Rect>();
            for (var i = 0; i < windows.Length; i++)
            {
                var window = windows[i];
                if (!window.IsPresentedInStack) continue;
                var bounds = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
                if (i < positions.Count) bounds.Union(positions[i]);
                occupied[window] = bounds;
            }

            // Move established notices first. Their slots remain occupied during exit fades.
            for (var i = 0; i < positions.Count; i++)
                if (windows[i].IsPresentedInStack)
                    windows[i].PlaceInStack(positions[i], area);
            for (var i = positions.Count; i < windows.Length; i++)
                windows[i].WaitForStackSlot();

            for (var i = 0; i < positions.Count; i++)
            {
                var window = windows[i];
                if (window.IsPresentedInStack) continue;
                // An incoming card must not fade in on top of a card still moving down.
                var collision = occupied.Any(pair => pair.Key != window && pair.Key.IsPresentedInStack
                    && pair.Value.IntersectsWith(positions[i]));
                if (collision)
                {
                    window.WaitForStackSlot();
                    RetryAfterMovement(window.Dispatcher);
                }
                else
                {
                    window.PlaceInStack(positions[i], area);
                    occupied[window] = positions[i];
                }
            }
        }
    }

    private void RetryAfterMovement(Dispatcher dispatcher)
    {
        if (_settleTimer is null)
        {
            _settleTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            { Interval = TimeSpan.FromMilliseconds(240) };
            _settleTimer.Tick += (_, _) => { _settleTimer.Stop(); Refresh(); };
        }
        _settleTimer.Stop();
        _settleTimer.Start();
    }
}
