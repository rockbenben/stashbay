using System.Windows;
using System.Diagnostics;
using System.ComponentModel;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Threading;
using FileStash.App.Controls;
using FileStash.App.Resources;
using FileStash.App.Services;
using FileStash.Core.Models;

namespace FileStash.App.Views;

/// <summary>结果的非模态提示。</summary>
/// <remarks>
/// 两种用法：
/// ① 写入结果（构造函数带 Operation + 条目）—— 有详情、可撤销、可提权重试；
/// ② 普通提示（标题 + 一句话）—— 加入暂存之类的回执。
///
/// 为什么不用 MessageBox：模态框会把用户的输入打断，还会占住主线程的模态循环
/// （后台会话里更是直接把进程吊住）。回执这类信息应该"飘一下就走"。
/// </remarks>
public partial class ResultToast : Window
{
    private ToastStack? _stack;
    private double _stackTargetTop = double.NaN;
    private int _stackMoveVersion;
    internal bool IsPresentedInStack { get; private set; }
    private bool _closing;
    private bool _closeReady;
    private static bool CanAnimate => !App.InSmoke;
    private DispatcherTimer? _lifeTimer;
    private ToastCountdown? _countdown;
    private long _lastLifeTick;
    private int _displayedSeconds = -1;
    private readonly Operation? _operation;
    private readonly IReadOnlyList<OperationEntry>? _entries;

    public ResultToast(Operation operation, IReadOnlyList<OperationEntry> entries)
    {
        InitializeComponent();

        // 内容多起来会顶到屏幕外（撤销 / 详情 / 重试 / 关闭 四颗键不能被挤掉）
        WindowEffects.FitToWorkArea(this);

        _operation = operation;
        _entries = entries;

        TitleText.Text = operation.Status switch
        {
            OperationStatus.Succeeded => Strings.ToastSucceeded,
            OperationStatus.PartiallyFailed => Strings.ToastPartiallyFailed,
            OperationStatus.Cancelled => Strings.ToastCancelled,
            _ => Strings.ToastTitle,
        };

        SummaryText.Text = Describe(operation, entries);

        var failed = entries.Count(entry => entry.Action == EntryResult.Failed);
        DetailsButton.Visibility = failed > 0 || entries.Any(entry => entry.Error is not null)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 有失败条目时提供"以管理员身份重试"
        RetryButton.Visibility = failed > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 失败时主键是"重试"而不是"撤销"：撤销不解决任何失败，
        // 而它此前一直是唯一的强调色按钮，用户的视线被领到不该点的那一颗上。
        // 重试那颗的强调样式写在 XAML 里（它只在失败时出现），这里只管把撤销降级。
        if (failed > 0)
        {
            UndoButton.ClearValue(StyleProperty);
        }

        // 需要人决定的回执不自己消失；纯告知的照旧 10 秒
        StartLife(failed > 0 ? 0 : 10);
    }

    /// <summary>普通提示：一句标题 + 一行摘要，没有可撤销的东西。</summary>
    public ResultToast(string title, string summary)
    {
        InitializeComponent();
        WindowEffects.FitToWorkArea(this);

        Title = title;
        TitleText.Text = title;
        SummaryText.Text = summary;
        MinHeight = 0;
        ActionPanel.Visibility = Visibility.Collapsed;

        DetailsButton.Visibility = Visibility.Collapsed;
        UndoButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;

        StartLife(6);
    }

    /// <summary>
    /// 放到右下角，过一会儿自己消失。<paramref name="seconds"/> 给 0 表示不自隐。
    /// </summary>
    private void StartLife(int seconds)
    {
        PlaceAtBottomRight();

        // 高度由内容定（SizeToContent），所以尺寸一变就要重新贴边
        SizeChanged += (_, _) =>
        {
            if (_stack is null) PlaceAtBottomRight();
            else _stack.Refresh();
        };

        if (seconds > 0)
        {
            _countdown = new ToastCountdown(TimeSpan.FromSeconds(seconds));
            DismissCountdown.Visibility = Visibility.Visible;
            UpdateCountdown();
        }

        // Headless captures remain fully opaque and do not expire during a matrix.
        if (App.InSmoke) return;
        if (seconds > 0)
        {
            _lifeTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _lifeTimer.Tick += OnLifeTick;
        }
        Opacity = 0;
        Loaded += OnToastLoaded;
        Closed += OnToastClosed;
    }

    private void OnToastLoaded(object sender, RoutedEventArgs e)
    {
        if (_closing || _stack is not null || !ReferenceEquals(e.OriginalSource, this)) return;
        _stack = ToastStack.Current;
        _stack.Add(this);
    }

    internal void PlaceInStack(Rect bounds, Rect workArea)
    {
        var entering = !IsPresentedInStack;
        if (!entering && Math.Abs(_stackTargetTop - bounds.Top) < 0.1 && Math.Abs(Left - bounds.Left) < 0.1)
            return;
        var currentTop = Top;
        _stackTargetTop = bounds.Top;
        var version = ++_stackMoveVersion;
        Left = bounds.Left;
        if (!entering && CanAnimate && Math.Abs(currentTop - bounds.Top) > 0.5
            && currentTop >= workArea.Top && currentTop + ActualHeight <= workArea.Bottom)
        {
            // Preserve the currently displayed position while replacing an in-flight animation.
            SetCurrentValue(TopProperty, currentTop);
            var move = new DoubleAnimation(currentTop, bounds.Top, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            move.Completed += (_, _) =>
            {
                if (version != _stackMoveVersion) return;
                SetCurrentValue(TopProperty, bounds.Top);
                BeginAnimation(TopProperty, null);
            };
            BeginAnimation(TopProperty, move);
            return;
        }
        SetCurrentValue(TopProperty, bounds.Top);
        BeginAnimation(TopProperty, null);
        if (!entering) return;
        IsPresentedInStack = true;
        if (!IsVisible) Show();
        BeginEntrance();
    }

    internal void WaitForStackSlot()
    {
        if (_closing) return;
        IsPresentedInStack = false;
        _lifeTimer?.Stop();
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        if (IsVisible) Hide();
    }

    private void BeginEntrance()
    {
        if (_closing) return;
        if (!CanAnimate)
        {
            Opacity = 1;
            StartCountdown();
            return;
        }
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) => StartCountdown();
        BeginAnimation(OpacityProperty, fade);
    }

    private void StartCountdown()
    {
        if (_closing || !IsVisible || _lifeTimer is null) return;
        _lastLifeTick = Stopwatch.GetTimestamp();
        _lifeTimer.Start();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeReady) return;
        _lifeTimer?.Stop();
        if (!CanAnimate || App.IsShuttingDown || !IsVisible || Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsHitTestVisible = false;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        fade.Completed += (_, _) =>
        {
            _closeReady = true;
            Close();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void OnToastClosed(object? sender, EventArgs e)
    {
        if (_lifeTimer is not null)
        {
            _lifeTimer.Stop();
            _lifeTimer.Tick -= OnLifeTick;
        }
        BeginAnimation(OpacityProperty, null);
        _stackMoveVersion++;
        BeginAnimation(TopProperty, null);
        IsPresentedInStack = false;
        _stack?.Remove(this);
        Loaded -= OnToastLoaded;
        Closed -= OnToastClosed;
    }

    private void OnLifeTick(object? sender, EventArgs e)
    {
        if (_countdown is null) return;
        var now = Stopwatch.GetTimestamp();
        _countdown.Advance(Stopwatch.GetElapsedTime(_lastLifeTick, now),
            IsMouseOver || IsKeyboardFocusWithin);
        _lastLifeTick = now;
        UpdateCountdown();
        if (_countdown.IsElapsed) Close();
    }

    private void UpdateCountdown()
    {
        if (_countdown is null) return;
        var seconds = (int)Math.Ceiling(_countdown.Remaining.TotalSeconds);
        if (seconds != _displayedSeconds)
        {
            _displayedSeconds = seconds;
            var hint = Strings.Format(Strings.ToastDismissCountdown, seconds);
            NoticeCloseButton.ToolTip = hint;
            System.Windows.Automation.AutomationProperties.SetHelpText(NoticeCloseButton, hint);
        }
        var fraction = _countdown.Fraction;
        Geometry geometry;
        if (fraction >= 1)
            geometry = new EllipseGeometry(new Point(14, 14), 12, 12);
        else if (fraction <= 0)
            geometry = Geometry.Empty;
        else
        {
            var angle = (1 - fraction) * 2 * Math.PI - Math.PI / 2;
            var arc = new StreamGeometry();
            using (var context = arc.Open())
            {
                context.BeginFigure(new Point(14 + 12 * Math.Cos(angle), 14 + 12 * Math.Sin(angle)), false, false);
                context.ArcTo(new Point(14, 2),
                    new Size(12, 12), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            geometry = arc;
        }
        CountdownArc.Data = geometry;
    }

    /// <summary>撤销请求（由调用方处理）。</summary>
    public event Action? UndoRequested;

    private static string Describe(Operation operation, IReadOnlyList<OperationEntry> entries)
    {
        var created = entries.Count(entry => entry.Action == EntryResult.Created);
        var overwritten = entries.Count(entry => entry.Action is EntryResult.Overwritten or EntryResult.Renamed);
        var skipped = entries.Count(entry => entry.Action == EntryResult.Skipped);
        var failed = entries.Count(entry => entry.Action == EntryResult.Failed);

        var parts = new List<string>();

        if (created > 0)
        {
            parts.Add(Strings.Format(Strings.ToastCreated, created));
        }

        if (overwritten > 0)
        {
            parts.Add(Strings.Format(Strings.ToastOverwritten, overwritten));
        }

        if (skipped > 0)
        {
            parts.Add(Strings.Format(Strings.ToastSkipped, skipped));
        }

        if (failed > 0)
        {
            parts.Add(Strings.Format(Strings.ToastFailed, failed));
        }

        var counts = parts.Count == 0
            ? Strings.ToastNothingWritten
            : string.Join(Strings.ItemSeparator, parts) + Strings.SentenceEnd;

        // 说清写到哪：回执常常是好几条操作里最新的那一条，只报数量认不出是哪次
        return string.IsNullOrEmpty(operation.TargetDescription)
            ? counts
            : counts + Environment.NewLine + Strings.Format(Strings.ToastTargetLine, operation.TargetDescription);
    }

    private void PlaceAtBottomRight()
    {
        // 按"窗口自己那块屏"的工作区贴右下角，不是主屏的 ——
        // 主窗口在副屏时，用主屏的 WorkArea 会把 Toast 贴到主屏去（用户看不见它）。
        var anchor = Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(window => window is not ResultToast && window.IsActive)
            ?? (Application.Current?.MainWindow is ResultToast ? null : Application.Current?.MainWindow);
        var workArea = WindowEffects.GetWindowWorkArea(anchor ?? this);

        // 高度改成 SizeToContent 之后，Width/Height 这两个属性在量出来之前是 NaN，
        // 必须用 Actual*，否则框会被贴到屏幕外
        var width = double.IsNaN(ActualWidth) || ActualWidth <= 0 ? Width : ActualWidth;
        var height = double.IsNaN(ActualHeight) || ActualHeight <= 0 ? 132 : ActualHeight;

        Left = workArea.Right - width - 16;
        Top = workArea.Bottom - height - 16;
    }

    private void OnDetails(object sender, RoutedEventArgs e)
    {
        var details = (_entries ?? [])
            .Where(entry => entry.Error is not null || entry.Action == EntryResult.Failed)
            .Select(entry => Strings.Format(
                Strings.ToastDetailLine,
                entry.TargetPath,
                entry.Error ?? Strings.ResultFailed))
            .ToList();

        ThemedMessageBox.Show(
            details.Count == 0 ? Strings.ToastNoFailures : string.Join(Environment.NewLine, details),
            Strings.ToastDetailsTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        UndoRequested?.Invoke();
        Close();
    }

    /// <summary>以管理员身份重试失败的条目（拉起一个提权进程）。</summary>
    private void OnRetry(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        if (_operation is null || !ElevatedRetryLauncher.Launch(_operation.Id))
        {
            new WpfDialogService().ShowInfo(Strings.AppName, Strings.ToastElevatedFailed);
        }

        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
