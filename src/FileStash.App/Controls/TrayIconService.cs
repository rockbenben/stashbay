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
            // H.NotifyIcon 的 IconSource 是通过 async void ToIconAsync 异步转换的，
            // 如果在此期间立刻调用 ForceCreate，Icon 句柄尚为 null 会导致 Win32 Shell_NotifyIcon 抛 TryCreate failed。
            // 兜底同步载入图标资源，确保句柄已就绪。
            if (tray.Icon is null)
            {
                try
                {
                    var iconUri = new Uri("pack://application:,,,/Resources/app.ico");
                    var streamInfo = Application.GetResourceStream(iconUri);
                    if (streamInfo is not null)
                    {
                        using var stream = streamInfo.Stream;
                        tray.Icon = new System.Drawing.Icon(stream);
                    }
                }
                catch
                {
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
