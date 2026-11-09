using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FileStash.App.Controls;
using FileStash.Core.Abstractions;
using FileStash.Core.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace FileStash.App.Diagnostics;

/// <summary>
/// `--smoke`：构造并布局每一个窗口，然后退出。
/// </summary>
/// <remarks>
/// WPF 的 XAML 是懒加载的，某个窗口的 XAML 写错，只要不打开那个窗口就不会报错，
/// 能一路带到发版。这个开关就是那道防线。
/// </remarks>
internal static class SmokeRunner
{
    public static async Task<int> RunAsync(IServiceProvider services)
    {
        // 异常处理器据此改成"记 marker 并退出"，不再弹模态框把 CI 的进程吊住
        App.InSmoke = true;

        // 关掉"最后一个窗口关闭即退出"：自检要连续开关好几个窗口，
        // 否则第一个窗口 Close() 就开始关闭应用，之后的 Window.Show() 静默空转
        //（不抛、hwnd=0、尺寸全零），表现为"某一个窗口布局成零尺寸"。
        if (Application.Current is { } app)
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        try
        {
            AssertLanguageMatchesSettings(services);

            foreach (var shot in WindowCatalog.All(services))
            {
                // 应用已经开始关闭时不要再开窗口：Show() 会静默空转，
                // 后面那句"尺寸全零"的断言会报出一个跟真实原因无关的错
                if (Application.Current.Dispatcher.HasShutdownStarted)
                {
                    throw new InvalidOperationException("自查过程中应用已开始关闭");
                }

                Serilog.Log.Debug("smoke: 造窗口 {Name}", shot.Name);

                var window = await shot.Factory();
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.ShowActivated = false;
                window.Show();

                Serilog.Log.Debug("smoke: 已显示 {Name}", shot.Name);

                // 泵到 Loaded：Show 排队的收尾活当场做完
                await UiPump.Settle(window);

                if (shot.Drive is { } drive)
                {
                    // 驱动也一起过：那一层要是把窗口撑炸（空集合、路径不存在），
                    // smoke 就该在这里红，而不是等到截图轮
                    await drive(window, services);
                    await UiPump.Settle(window);
                }

                Serilog.Log.Debug("smoke: 已布局 {Name}（{W}x{H}）", shot.Name, window.ActualWidth, window.ActualHeight);

                // 不只"没抛"：必须断言真的布局出了尺寸。Window.Show() 在应用已开始关闭时会静默空转
                // （不抛、hwnd=0、ActualWidth=0），只判异常的 smoke 在那种状态下照样绿灯——
                // 实测漏过一整轮 84/84 全零。
                if (window.ActualWidth <= 0 || window.ActualHeight <= 0)
                {
                    window.Close();
                    throw new InvalidOperationException($"{shot.Name}: laid out to zero size");
                }

                window.Close();
            }

            Serilog.Log.Debug("smoke: 窗口都过了，检查托盘");

            // 托盘断言：ForceCreate 漏掉的症状是"应用照常跑、热键照常用，就是托盘里什么都没有"，
            // 而 smoke 默认抓不到（那两个开关按惯例挂在托盘初始化之前就 return 了）。
            if (!TrayIconService.IsTaskbarAvailable)
            {
                Serilog.Log.Information("smoke: 当前会话未检测到任务栏窗口（Shell_TrayWnd），跳过托盘创建断言");
            }
            else
            {
                var tray = TrayIconService.Create();
                if (!tray.IsCreated)
                {
                    throw new InvalidOperationException("Tray: ForceCreate 后仍未创建");
                }

                tray.Dispose();
            }

            // 自查途中冒出来的错误（App.ShowError 记的）也算失败：
            // 自查不能"出了错还报 OK"
            var failure = App.SmokeFailure;

            Serilog.Log.Debug("smoke: 写 marker（{Result}）", failure is null ? "OK" : failure);
            File.WriteAllText(App.SmokeReportPath, failure ?? "OK");
            return failure is null ? 0 : 1;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "smoke 失败");
            File.WriteAllText(App.SmokeReportPath, ex.ToString());
            return 1;
        }
    }

    /// <summary>
    /// 界面语言必须跟设置对得上。
    /// </summary>
    /// <remarks>
    /// 这条判据是踩过坑才加的：区域性如果在 <c>async void</c> 的启动路径上改，
    /// 方法一返回就被作用域吃掉了，界面静默退回系统语言 ——
    /// 没有异常、没有日志、界面"就是不对"。自查不查这一条，CI 就永远发现不了。
    /// </remarks>
    private static void AssertLanguageMatchesSettings(IServiceProvider services)
    {
        var settings = services.GetRequiredService<ISettingsService>();
        var expected = AppLanguageResolver.Resolve(settings.Current.Language);
        var actual = CultureInfo.CurrentUICulture;

        Serilog.Log.Debug(
            "smoke: 界面语言 {Language}（应为 {Expected}，实际 {Actual}）",
            settings.Current.Language,
            expected.Name,
            actual.Name);

        if (!string.Equals(expected.Name, actual.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"界面语言与设置不符：设置 {settings.Current.Language}（应为 {expected.Name}），实际 {actual.Name}");
        }
    }
}
