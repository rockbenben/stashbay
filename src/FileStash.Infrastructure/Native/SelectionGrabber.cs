using System.Runtime.InteropServices;

namespace FileStash.Infrastructure.Native;

/// <summary>
/// 检查前台窗口所属进程。
/// </summary>
public static class SelectionGrabber
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>检查前台窗口是否属于本应用进程。</summary>
    public static bool IsForegroundCurrentProcess()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(foreground, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

}
