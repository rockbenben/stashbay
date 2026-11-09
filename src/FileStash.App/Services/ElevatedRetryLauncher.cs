using System.IO;
using System.Diagnostics;

namespace FileStash.App.Services;

/// <summary>以管理员身份重试失败条目。</summary>
public static class ElevatedRetryLauncher
{
    /// <summary>拉起一个提权进程执行重试；用户取消 UAC 就什么也不做。</summary>
    public static bool Launch(string operationId)
    {
        var executablePath = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executablePath) || !Guid.TryParseExact(operationId, "N", out _))
        {
            return false;
        }

        try
        {
            // 提权进程**不参与单实例检查**（命令行的 --elevated-retry 会被豁免），
            // 否则它会被管道转发出去、随即退出，重试根本不会执行
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas",
            };
            startInfo.ArgumentList.Add("--elevated-retry");
            startInfo.ArgumentList.Add(operationId);
            var redirected = Environment.GetEnvironmentVariable(FileStash.Core.Utils.AppPaths.HomeVariable);
            if (string.IsNullOrWhiteSpace(redirected))
                redirected = Environment.GetEnvironmentVariable(FileStash.Core.Utils.AppPaths.LegacyHomeVariable);
            if (!string.IsNullOrWhiteSpace(redirected))
            {
                startInfo.ArgumentList.Add("--elevated-home");
                startInfo.ArgumentList.Add(Path.GetFullPath(redirected));
            }
            Process.Start(startInfo);

            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 用户取消了 UAC
            return false;
        }
    }
}
