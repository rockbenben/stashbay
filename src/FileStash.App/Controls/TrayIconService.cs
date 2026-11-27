using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using H.NotifyIcon;

namespace FileStash.App.Controls;

/// <summary>
/// 托盘图标。
/// </summary>
/// <remarks>
/// H.NotifyIcon 把 shell 图标的创建挂在 Loaded 上，而 App.xaml 资源里的 TaskbarIcon
/// 从不进可视树、这个事件一辈子不触发 —— 换库的人最容易在这里翻车：
/// 症状是应用照常跑、热键照常用，就是托盘里什么都没有。
/// </remarks>
internal static class TrayIconService
{
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>当前会话是否有任务栏（Shell_TrayWnd）。自动化/无头/非交互会话下可能为 false。</summary>
    public static bool IsTaskbarAvailable
    {
        get
        {
            try
            {
                return FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }
    }

    public static TaskbarIcon Create()
    {
        var tray = (TaskbarIcon)Application.Current.FindResource("Tray");

        if (!tray.IsCreated)
        {
            // A smoke run can use the same executable as the resident app.
            // Give its icon a separate ID so registration and disposal leave the resident icon alone.
            if (App.InSmoke)
            {
                tray.Id = H.NotifyIcon.Core.TrayIcon.CreateUniqueGuidForProcessPath("smoke");
            }

            // Load the ICO synchronously, selecting the native small-icon frame.
            // IconSource's asynchronous WPF conversion can resample an arbitrary
            // frame and later replace this handle; keep one owner of the icon.
            if (tray.Icon is null)
            {
                try
                {
                    var iconUri = new Uri("pack://application:,,,/Resources/app.ico");
                    var streamInfo = Application.GetResourceStream(iconUri);
                    if (streamInfo is not null)
                    {
                        using var stream = streamInfo.Stream;
                        var width = Math.Max(16, GetSystemMetrics(49)); // SM_CXSMICON
                        var height = Math.Max(16, GetSystemMetrics(50)); // SM_CYSMICON
                        tray.Icon = new System.Drawing.Icon(stream, width, height);
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Failed to load the tray icon resource");
                }
            }

            try
            {
                tray.ForceCreate(enablesEfficiencyMode: false);
            }
            catch (Exception ex)
            {
                // 无任务栏环境（如无头 CI、服务会话或 Explorer 尚未启动时），
                // ForceCreate 会因找不到 Shell_TrayWnd 抛 TryCreate failed。
                // 此时记录警告，不崩溃应用。
                Serilog.Log.Warning(ex, "托盘图标初始化未能完成（任务栏可能未就绪）");
            }
        }

        return tray;
    }
}
