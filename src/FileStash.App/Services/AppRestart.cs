using System.Diagnostics;
using System.IO;

namespace FileStash.App.Services;

/// <summary>重启自己。</summary>
/// <remarks>
/// 切语言之后必须重启：界面文字是 <c>x:Static</c>，在 XAML 解析的时候就定死了，
/// 换 <c>CurrentUICulture</c> 不会让已经解析过的界面重新取一遍资源。
///
/// 新实例带 <c>--show</c>：那条路径的互斥体等待是 3 秒（见 App.xaml.cs 的 takeover 分档），
/// 正好留给当前实例退出、释放互斥体。
/// </remarks>
internal static class AppRestart
{
    /// <summary>拉起一个新的自己。返回 false 表示拉不起来（调用方要自己兜住）。</summary>
    public static bool Relaunch(params string[] arguments)
    {
        // 单文件发布下 Assembly.Location 是空的，进程路径才是 exe 自己
        var executablePath = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Serilog.Log.Error(ex, "重启自己失败");
            return false;
        }
    }
}
