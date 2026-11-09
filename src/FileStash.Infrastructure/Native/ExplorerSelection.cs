using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace FileStash.Infrastructure.Native;

/// <summary>
/// 读资源管理器当前选中的文件。
/// </summary>
/// <remarks>
/// 走 Shell.Application 的 ShellWindows（后期绑定），找到前台那个资源管理器窗口，
/// 再读它的 Document.SelectedItems()。
/// </remarks>
public static class ExplorerSelection
{
    private const string ExplorerClassName = "CabinetWClass";

    /// <summary>前台窗口是不是资源管理器。</summary>
    public static bool IsForegroundExplorer() => GetForegroundExplorer() is not null;

    /// <summary>取资源管理器当前选中的路径；没有就返回空列表。</summary>
    public static IReadOnlyList<string> GetSelectedPaths()
    {
        var explorer = GetForegroundExplorer();

        if (explorer is null)
        {
            return [];
        }

        try
        {
            dynamic document = explorer.Document;
            dynamic items = document.SelectedItems();

            var paths = new List<string>();

            foreach (dynamic item in items)
            {
                var path = (string?)item.Path;

                if (!string.IsNullOrEmpty(path))
                {
                    paths.Add(path);
                }
            }

            return paths;
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException)
        {
            return [];
        }
    }

    private static dynamic? GetForegroundExplorer()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");

            if (shellType is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic windows = shell.Windows();

            var foreground = GetForegroundWindow();

            foreach (dynamic window in windows)
            {
                if ((int)window.HWND == foreground.ToInt32())
                {
                    return window;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException)
        {
        }

        return null;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder buffer, int count);

    /// <summary>前台窗口的类名。</summary>
    public static string GetForegroundClassName()
    {
        var buffer = new System.Text.StringBuilder(256);
        GetClassName(GetForegroundWindow(), buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
