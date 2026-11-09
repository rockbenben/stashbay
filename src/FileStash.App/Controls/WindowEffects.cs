using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileStash.App.Controls;

/// <summary>
/// WPF 窗口的三件套：防白闪、按工作区封顶、把窗口抢到前台。
/// 每条都有实测依据，写错了症状离根因很远 —— 涉及 DPI、无边框阴影、防白屏 Cloak 与光标就近定位。
/// </summary>
internal static class WindowEffects
{
    private const int DwmwaCloak = 13;
    private const int DwmwaCloaked = 14;
    private const int DwmwaUseImmersiveDarkMode = 20;   // Win11 build 22000 起
    private const int DwmwaUseImmersiveDarkModeLegacy = 19; // Win10 build 18985 之前

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WinPoint lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(WinPoint pt, uint dwFlags);

    private const uint MonitorDefaultToNearest = 0x00000002;

    /// <summary>读当前的 cloak 状态，供自检断言用（DWMWA_CLOAKED 的签名是 out 不是 ref）。</summary>
    public static bool IsCloaked(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    /// <summary>
    /// 防白闪：窗口显示到 WPF 提交第一帧之间有真实空档（热开约 100ms、首次约 300ms），
    /// 而第一帧的 DWM 合成结果是白的。把窗口先从合成里藏住，感知延迟不变。
    /// </summary>
    /// <remarks>
    /// 挂 WM_ERASEBKGND 或换窗口类背景刷都无效（白帧发生在任何擦除消息之前）。
    /// 托盘主窗口关闭只是 Hide()、对象一直活着，SourceInitialized 一辈子只触发一次，
    /// 所以隐藏时要重新 cloak，为下一次显示备好。
    /// </remarks>
    public static void EnableAntiFlash(Window window)
    {
        void Cloak(int on)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                DwmSetWindowAttribute(hwnd, DwmwaCloak, ref on, sizeof(int));
            }
        }

        window.SourceInitialized += (_, _) => Cloak(1);
        window.ContentRendered += (_, _) => Cloak(0);

        window.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false)
            {
                // 隐藏态不参与合成，此时 cloak 不可见
                Cloak(1);
                return;
            }

            // 兜底：正常路径由 ContentRendered 摘掉；万一某次没触发，
            // 卡在 cloak 里的窗口就是"点了托盘没反应"的隐形应用。
            // ContextIdle 排在 Render 和 Loaded 之后，正常路径跑到它时早已摘干净。
            window.Dispatcher.BeginInvoke(() => Cloak(0), DispatcherPriority.ContextIdle);
        };
    }

    /// <summary>
    /// 按窗口所在那块屏的工作区给高度封顶。SizeToContent 的窗口高度由内容决定，
    /// 不封顶的话内容一多"确定/取消"整行会跑到屏幕外。
    /// </summary>
    /// <remarks>
    /// 封顶值不能写死：同一个数要同时伺候 1366x768 的笔记本和 4K 屏。
    /// 也不能用 SystemParameters.WorkArea —— 它给的是主屏的工作区，
    /// 声明了 PerMonitorV2 之后不存在"整个桌面一个尺寸"，主窗口在副屏时就错了。
    /// 探测失败时什么都不做，退回 XAML 里那个保守的 MaxHeight。
    /// </remarks>
    public static void FitToWorkArea(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info))
            {
                // 探测失败：rcWork 全是零，cap 会算成负数，把窗口按到 MinHeight 上 —— 那是一条
                // 什么都不做，退回 XAML 的保守值
                return;
            }

            var scale = VisualTreeHelper.GetDpi(window).DpiScaleY;
            var workHeight = (info.rcWork.Bottom - info.rcWork.Top) / scale;
            var cap = workHeight - 72;   // 留点上下呼吸

            window.MaxHeight = Math.Max(cap, window.MinHeight);
            if (!double.IsNaN(window.Height) && window.Height > cap)
            {
                window.Height = cap;
            }
        };
    }

    /// <summary>
    /// 把窗口放到所在屏幕工作区的右下角。
    /// </summary>
    /// <remarks>
    /// "一键唤出"的小面板就该出现在那儿 —— 像托盘弹窗那样，
    /// 而不是屏幕正中间挡住视线。
    /// </remarks>
    public static void PlaceAtBottomRight(Window window, double margin = 16)
    {
        var work = GetWindowWorkArea(window);
        window.Left = work.Left + work.Width - window.Width - margin;
        window.Top = work.Top + work.Height - window.Height - margin;
    }

    /// <summary>
    /// 把窗口放置在当前鼠标光标附近（并确保完全落在所在显示器的工作区内）。
    /// </summary>
    public static void PlaceNearCursor(Window window, double margin = 16)
    {
        if (!GetCursorPos(out var pt))
        {
            PlaceAtBottomRight(window, margin);
            return;
        }

        var hMonitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };

        if (!GetMonitorInfo(hMonitor, ref info))
        {
            PlaceAtBottomRight(window, margin);
            return;
        }

        var scaleX = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var scaleY = VisualTreeHelper.GetDpi(window).DpiScaleY;

        var curX = pt.X / scaleX;
        var curY = pt.Y / scaleY;

        var workLeft = info.rcWork.Left / scaleX;
        var workTop = info.rcWork.Top / scaleY;
        var workWidth = (info.rcWork.Right - info.rcWork.Left) / scaleX;
        var workHeight = (info.rcWork.Bottom - info.rcWork.Top) / scaleY;

        var targetLeft = curX - (window.Width / 2);
        var targetTop = curY - (window.Height / 3);

        // 工作区可能比窗口更小（高 DPI、小屏或刚从大屏移动过来）。
        // 此时优先保住左上角，避免 Clamp 的上下界倒置导致唤出失败。
        var minLeft = workLeft + margin;
        var minTop = workTop + margin;
        window.Left = Math.Clamp(targetLeft, minLeft, Math.Max(minLeft, workLeft + workWidth - window.Width - margin));
        window.Top = Math.Clamp(targetTop, minTop, Math.Max(minTop, workTop + workHeight - window.Height - margin));

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero && window.Topmost)
        {
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow | SwpNoActivate);
        }
    }

    /// <summary>
    /// 获取窗口当前所在显示器的工作区（DIP 坐标）。
    /// </summary>
    public static Rect GetWindowWorkArea(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return new Rect(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top,
                            SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
        }

        var hMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(hMonitor, ref info))
        {
            return new Rect(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Top,
                            SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height);
        }

        var scaleX = VisualTreeHelper.GetDpi(window).DpiScaleX;
        var scaleY = VisualTreeHelper.GetDpi(window).DpiScaleY;

        return new Rect(
            info.rcWork.Left / scaleX,
            info.rcWork.Top / scaleY,
            (info.rcWork.Right - info.rcWork.Left) / scaleX,
            (info.rcWork.Bottom - info.rcWork.Top) / scaleY);
    }

    /// <summary>
    /// 确保窗口完全落在当前屏幕的工作区内部（若内容增长导致触底则向上推移）。
    /// </summary>
    public static void ClampToWorkArea(Window window, double margin = 16)
    {
        var work = GetWindowWorkArea(window);

        if (window.Left + window.Width + margin > work.Left + work.Width)
        {
            window.Left = work.Left + work.Width - window.Width - margin;
        }
        if (window.Left < work.Left + margin)
        {
            window.Left = work.Left + margin;
        }

        if (window.Top + window.Height + margin > work.Top + work.Height)
        {
            window.Top = work.Top + work.Height - window.Height - margin;
        }
        if (window.Top < work.Top + margin)
        {
            window.Top = work.Top + margin;
        }
    }

    /// <summary>
    /// 把窗口抢到前台。Windows 的前台移交是异步的，抢一次就走不可靠：
    /// 抢完之后持续验证一小段时间，丢了就再抢，连续几次确认握住才收手。
    /// </summary>
    public static async void BringToFront(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // 记住一开始的前台是谁：超时兜底只允许压过它
        var blocker = GetForegroundWindow();

        try
        {
            if (!window.IsVisible)
            {
                window.Show();
            }

            // 如果窗口本身是置顶窗口（如迷你中转搁架），立即通过 SetWindowPos 冲顶，免去等待 900ms 抢焦点的延迟
            if (window.Topmost)
            {
                SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow | SwpNoActivate);
            }

            window.Activate();

            var stable = 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(window.Topmost ? 150 : 900);

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(true);

                // 用户在窗口内把它关了 —— 这不是"抢输了"，退出即可
                if (!window.IsVisible || hwnd == IntPtr.Zero)
                {
                    return;
                }

                if (GetForegroundWindow() == hwnd)
                {
                    stable++;
                    if (stable >= 3)
                    {
                        return;
                    }
                }
                else
                {
                    stable = 0;
                    window.Activate();
                }
            }

            // 守卫超时且一次都没握住：退而求其次把 z 序提到最前
            if (stable == 0)
            {
                Serilog.Log.Debug(
                    "BringToFront：没抢到前台，改提 z 序（对手 {Blocker}）", blocker);

                RaiseAboveBlocker(window, hwnd, blocker);
            }
            else
            {
                Serilog.Log.Debug("BringToFront：抢到了前台");
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "BringToFront 失败");
        }
    }

    /// <summary>
    /// z 序兜底。三道护栏都是代码审查从裸版本里实际挑出来的，缺一条都会引入比它修的问题更严重的 bug。
    /// </summary>
    private static void RaiseAboveBlocker(Window window, IntPtr hwnd, IntPtr blocker)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // 置顶窗口（如小搁架）：直接通过 SetWindowPos 提到最顶层
        if (window.Topmost)
        {
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow | SwpNoActivate);
            return;
        }

        // ② 只压过一开始那个对手：
        // 用户可能已经切到第三个应用去打字，不判断的话会盖住用户正在输入的地方。
        var fg = GetForegroundWindow();
        if (fg == hwnd || fg != blocker)
        {
            Serilog.Log.Debug(
                "RaiseAboveBlocker：跳过（现在的前台 {Fg} 不是一开始那个 {Blocker}）", fg, blocker);

            return;
        }

        const uint flags = SwpNoMove | SwpNoSize | SwpNoActivate;

        var topmost = SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, flags);
        var back = SetWindowPos(hwnd, HwndNotTopmost, 0, 0, 0, 0, flags);

        Serilog.Log.Debug(
            "RaiseAboveBlocker：提到最前 {Topmost}，再退回普通层 {Back}（窗口 {Handle}）",
            topmost, back, hwnd);
    }

    /// <summary>深色模式下把标题栏也刷成深色，否则非客户区会是白的。</summary>
    public static void ApplyDarkTitleBar(Window window, bool dark)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            var value = dark ? 1 : 0;

            // 属性 20 是 Win11 build 22000 起的编号，旧编号是 19；
            // DwmSetWindowAttribute 对不认识的编号返回错误码、不抛异常，所以要靠返回值判断。
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    /// <summary>Win32 的 RECT 是 left/top/right/bottom，不是 System.Windows.Rect（那是 x/y/width/height）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
