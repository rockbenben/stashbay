using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileStash.App.Controls;

namespace FileStash.App.Diagnostics;

/// <summary>
/// <c>--shots &lt;目录&gt;</c>：把每个窗口的每种状态按主题 × 工作区高度离屏渲染成 PNG。
/// 不抢焦点、不打扰运行中的实例。
/// </summary>
/// <remarks>
/// 这个 harness 有两个结构性的盲区，别拿它当验收：
/// ① 它无条件设 MaxHeight，等于替"忘了运行时封顶"的窗口补上了真实应用根本没有的上限——
///    截图张张正常，真机上按钮在屏幕外面；
/// ② 条件性 UI 的显隐由数据决定，harness 的状态没覆盖到的那一行就是零像素。
///    ②这一条现在由 <see cref="WindowCatalog.Shot.Drive"/> 逐个状态驱动 +
///    <see cref="UiPump"/> 等异步查询落地来收，但"驱动到的状态之外"仍然是盲区。
/// 离屏渲染仅覆盖静态视觉，动态交互与边界盲区由自动化测试补充。
/// </remarks>
internal static class ShotsRunner
{
    // 任务栏随 DPI 缩放，所以它在 DIP 里是个常数：Win10 约 40，Win11 约 48（自动隐藏则为 0）。
    // 取 48（更紧的一档），和 pitfalls 里"最紧一档 464 DIP"对齐。
    private const double Taskbar = 48;

    private static readonly double[] WorkAreaHeights =
    {
        768 / 1.50 - Taskbar,    // 1366x768 @150% ≈ 464 —— 最紧的一档
        768 / 1.25 - Taskbar,    // 1366x768 @125% ≈ 566
        1080 / 1.50 - Taskbar,   // 1920x1080 @150% ≈ 672
        1080 / 1.00 - Taskbar,   // 1920x1080 @100% ≈ 1032
    };

    public static async Task<int> RunAsync(string[] args, int shotsIndex, IServiceProvider services)
    {
        App.InSmoke = true;

        // 同 smoke：自检期间不让"最后一个窗口关闭"触发退出，否则后续窗口的 Show 会静默空转
        if (Application.Current is { } app)
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        var outputDirectory = args.Length > shotsIndex + 1
            ? args[shotsIndex + 1]
            : Path.Combine(Path.GetTempPath(), "filestash-shots");

        var failures = new List<string>();

        try
        {
            Directory.CreateDirectory(outputDirectory);
            File.Delete(Path.Combine(outputDirectory, "shots-size.tsv"));

            // Focused shelf reviews still use the same fixture, themes, sizes and hash guard.
            var compactOnly = args.Contains("--shots-compact-only", StringComparer.OrdinalIgnoreCase);
            var settingsOnly = args.Contains("--shots-settings-only", StringComparer.OrdinalIgnoreCase);
            var workbenchOnly = args.Contains("--shots-workbench-only", StringComparer.OrdinalIgnoreCase);
            var includeToasts = args.Contains("--shots-include-toasts", StringComparer.OrdinalIgnoreCase);
            foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                ThemeService.Apply(theme);

                foreach (var shot in WindowCatalog.All(services).Where(shot =>
                    (!settingsOnly || shot.Name.StartsWith("SettingsWindow", StringComparison.Ordinal)) &&
                    (!workbenchOnly || shot.Name.StartsWith("MainWindow.Expanded", StringComparison.Ordinal)) &&
                    (!compactOnly || shot.Name.StartsWith("MainWindow.Compact", StringComparison.Ordinal) ||
                    (includeToasts && shot.Name.StartsWith("ResultToast", StringComparison.Ordinal)))))
                {
                    foreach (var height in compactOnly || settingsOnly || workbenchOnly ? new[] { WorkAreaHeights[0], WorkAreaHeights[^1] } : WorkAreaHeights)
                    {
                        var name = $"{shot.Name}.{theme}";

                        try
                        {
                            await ShotAsync(name, shot, height, outputDirectory, services,
                                minimumWidth: (settingsOnly || workbenchOnly) && height == WorkAreaHeights[0]);
                        }
                        catch (Exception ex)
                        {
                            // 一格失败不能让整轮停摆：剩下的状态还要拍，
                            // 但失败必须点名，不许"少拍了"读成"拍过了"
                            failures.Add($"{name}: {ex.GetType().Name} {ex.Message}");
                        }
                    }
                }
            }

            ThemeService.Apply(AppTheme.Light);

            // 自查途中冒出来的错误也算失败
            var appFailure = App.SmokeFailure;
            var report = failures.Count == 0 && appFailure is null
                ? "OK"
                : $"FAIL shots={failures.Count} appFailure={appFailure ?? "none"}{Environment.NewLine}{string.Join(Environment.NewLine, failures)}";

            File.WriteAllText(App.SmokeReportPath, report);

            return failures.Count == 0 && appFailure is null ? 0 : 1;
        }
        catch (Exception ex)
        {
            File.WriteAllText(App.SmokeReportPath, ex.ToString());
            return 1;
        }
    }

    private static async Task ShotAsync(
        string name,
        WindowCatalog.Shot shot,
        double workAreaHeight,
        string outputDirectory,
        IServiceProvider services,
        bool minimumWidth = false)
    {
        var window = await shot.Factory();

        if (minimumWidth && window.MinWidth > 0) window.Width = window.MinWidth;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.Show();

        // 必须 Show 之后设，否则被真实显示器的值覆盖
        window.MaxHeight = workAreaHeight;

        // 改 MaxHeight 只是让布局失效，ActualWidth/ActualHeight 要等下一次布局过程才更新。
        // 少了这两句，截图就是按"封顶之前"的尺寸拍的——正好是这个 harness 要模拟的那个维度，等于白跑。
        await UiPump.Settle(window);

        if (shot.Drive is { } drive)
        {
            // 驱动要放在封顶之后：选中项、填目标这些动作会改布局，
            // 放在之前拍到的就是"没驱动"的那一帧
            await drive(window, services);
            await UiPump.Settle(window);
        }

        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);

        if (width <= 0 || height <= 0)
        {
            window.Close();
            throw new InvalidOperationException($"{name}: laid out to zero size");
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        var fileName = Path.Combine(outputDirectory, $"{name}@{workAreaHeight:0}.png");
        using var stream = File.Create(fileName);
        encoder.Save(stream);

        // 浮层要在窗口关掉之前拍：它们跟着一起没
        CapturePopups(window, $"{name}@{workAreaHeight:0}", outputDirectory);

        window.Close();

        // 尺寸落一张表：稿子里每格都要印"声明 vs 实测"，靠眼睛量不出哪个格子没生效
        File.AppendAllText(
            Path.Combine(outputDirectory, "shots-size.tsv"),
            $"{name}\t{workAreaHeight.ToString("0", System.Globalization.CultureInfo.InvariantCulture)}\t{width}\t{height}{Environment.NewLine}");
    }

    /// <summary>
    /// 把打开着的浮层（下拉、右键菜单）单独拍一张。
    /// </summary>
    /// <remarks>
    /// Popup 有自己的视觉根，<c>RenderTargetBitmap(window)</c> 拍不到它们 ——
    /// 而"深色主题下菜单是白板"这类缺陷恰好只长在浮层上。
    /// </remarks>
    private static void CapturePopups(Window window, string name, string outputDirectory)
    {
        var index = 0;

        foreach (var combo in UiPump.Descendants<ComboBox>(window).Where(combo => combo.IsDropDownOpen))
        {
            if (combo.Template?.FindName("PART_Popup", combo) is Popup { Child: FrameworkElement child })
            {
                RenderPopup(child, $"{name}.popup{index}", outputDirectory);
                index++;
            }
        }

        foreach (var owner in UiPump.Descendants<FrameworkElement>(window))
        {
            if (owner.ContextMenu is { IsOpen: true } menu)
            {
                RenderPopup(menu, $"{name}.popup{index}", outputDirectory);
                index++;
            }
        }
    }

    private static void RenderPopup(FrameworkElement element, string name, string outputDirectory)
    {
        var root = PresentationSource.FromVisual(element)?.RootVisual as FrameworkElement ?? element;

        root.UpdateLayout();

        var width = (int)Math.Ceiling(root.ActualWidth > 0 ? root.ActualWidth : root.Width);
        var height = (int)Math.Ceiling(root.ActualHeight > 0 ? root.ActualHeight : root.Height);

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"{name}: 浮层尺寸为 0（没真的打开？）");
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(Path.Combine(outputDirectory, $"{name}.png"));
        encoder.Save(stream);
    }
}
