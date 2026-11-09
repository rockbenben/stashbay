using System.Diagnostics;

namespace FileStash.Core.Tests;

/// <summary>测试用的 git 辅助。</summary>
internal static class GitHelper
{
    public static bool IsAvailable { get; } = FindGit() is not null;

    public static void InitRepository(string path)
    {
        Run(path, "init");
        Run(path, "config", "user.email", "test@example.com");
        Run(path, "config", "user.name", "FileStash Test");
        Run(path, "config", "commit.gpgsign", "false");
    }

    public static void CommitAll(string path, string message)
    {
        Run(path, "add", "-A");
        Run(path, "commit", "-m", message);
    }

    public static void CheckoutNewBranch(string path, string branch)
    {
        Run(path, "checkout", "-b", branch);
    }

    public static void Checkout(string path, string branch)
    {
        Run(path, "checkout", branch);
    }

    private static string? FindGit()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "git.exe");

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void Run(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindGit()!,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);

        if (process is null)
        {
            throw new InvalidOperationException("启动 git 失败");
        }

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();

        process.WaitForExit(30_000);

        if (process.ExitCode != 0)
        {
            // 静默失败会让测试断言莫名其妙的值：这里直接抛出来
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} 失败（退出码 {process.ExitCode}）：{standardError}{standardOutput}");
        }
    }
}
