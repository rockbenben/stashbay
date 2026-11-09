using System.Diagnostics;

namespace FileStash.App.Services;

/// <summary>以管理员身份重试失败条目。</summary>
public static class ElevatedRetryLauncher
{
    /// <summary>拉起一个提权进程执行重试；用户取消 UAC 就什么也不做。</summary>
    public static bool Launch(string operationId)
    {
        var executablePath = Environment.ProcessPath;

        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        try
        {
            // 提权进程**不参与单实例检查**（命令行的 --elevated-retry 会被豁免），
            // 否则它会被管道转发出去、随即退出，重试根本不会执行
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = $"--elevated-retry {operationId}",
                UseShellExecute = true,
                Verb = "runas",
            });

            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 用户取消了 UAC
            return false;
        }
    }
}
