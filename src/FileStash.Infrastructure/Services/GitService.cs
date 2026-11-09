using System.Diagnostics;
using System.Text;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;

namespace FileStash.Infrastructure.Services;

/// <summary>
/// 调用系统 git.exe。
/// </summary>
/// <remarks>
/// 找不到 git 时 <see cref="IsAvailable"/> 为 false，所有 Git 功能自动降级，不报错。
/// </remarks>
public sealed class GitService : IGitService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private readonly string? _gitPath;

    public GitService()
    {
        _gitPath = FindGitExecutable();
    }

    public bool IsAvailable => _gitPath is not null;

    public async Task<string?> GetRepoRootAsync(string path, CancellationToken ct)
    {
        var directory = ResolveDirectory(path);

        if (directory is null)
        {
            return null;
        }

        var output = await RunAsync(directory, "rev-parse --show-toplevel", ct);

        // 不在仓库里时 git 返回非 0
        return output;
    }

    public Task<string?> GetCurrentBranchAsync(string repoRoot, CancellationToken ct)
    {
        // symbolic-ref 在 detached HEAD 时退出码非 0，回退成短提交号
        return RunAsync(repoRoot, "symbolic-ref --quiet --short HEAD", ct)
            ?? RunAsync(repoRoot, "rev-parse --short HEAD", ct);
    }

    public Task<string?> GetHeadCommitAsync(string repoRoot, CancellationToken ct) =>
        RunAsync(repoRoot, "rev-parse HEAD", ct);

    public async Task<IReadOnlyList<GitStatusEntry>> GetStatusAsync(string repoRoot, CancellationToken ct)
    {
        var output = await RunAsync(repoRoot, "status --porcelain=v1 -z", ct);

        return output is null ? [] : ParsePorcelain(output);
    }

    /// <summary>
    /// 解析 <c>status --porcelain=v1 -z</c> 的输出。
    /// </summary>
    /// <remarks>
    /// 两条实测出来的规矩：
    /// ① 路径**永远从第 3 位开始**（状态两位 + 一个分隔空格）。
    ///    重命名看起来偏移了 3 位（"R  new"），那是因为 Y 位本身是空格，不是多了一个分隔符。
    /// ② 重命名 / 复制在 -z 模式下占**两个字段**（新路径在前、原路径在后），
    ///    按"每行一字段"解析会把原路径当成下一个条目 —— porcelain 解析最常见的错。
    /// </remarks>
    public static IReadOnlyList<GitStatusEntry> ParsePorcelain(string output)
    {
        var entries = new List<GitStatusEntry>();
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];

            if (field.Length < 4)
            {
                continue;
            }

            var status = field[..2];
            var path = field[3..];

            string? originalPath = null;

            if ((status.Contains('R', StringComparison.Ordinal) || status.Contains('C', StringComparison.Ordinal))
                && index + 1 < fields.Length)
            {
                originalPath = fields[++index];
            }

            entries.Add(new GitStatusEntry(status, path, originalPath));
        }

        return entries;
    }

    public async Task<IReadOnlySet<string>> FilterIgnoredAsync(
        string repoRoot,
        IReadOnlyList<string> relativePaths,
        CancellationToken ct)
    {
        if (relativePaths.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // check-ignore 一次最多给一批路径，逐批查
        foreach (var batch in relativePaths.Chunk(100))
        {
            // 带 -z 时，**输入**也要用 NUL 分隔（不只是输出）
            var input = string.Join('\0', batch) + "\0";
            var output = await RunWithInputAsync(repoRoot, "check-ignore --stdin -z", input, ct);

            if (output is null)
            {
                continue;
            }

            foreach (var field in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                ignored.Add(field);
            }
        }

        return ignored;
    }

    public async Task AddAsync(string repoRoot, IReadOnlyList<string> relativePaths, CancellationToken ct)
    {
        if (relativePaths.Count == 0)
        {
            return;
        }

        foreach (var batch in relativePaths.Chunk(100))
        {
            var arguments = string.Join(' ', batch.Select(path => $"\"{path}\""));
            await RunAsync(repoRoot, $"add -- {arguments}", ct);
        }
    }

    public async Task<string?> GetGitDirAsync(string repoRoot, CancellationToken ct) =>
        await RunAsync(repoRoot, "rev-parse --absolute-git-dir", ct);

    private async Task<string?> RunAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        if (_gitPath is null || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _gitPath,
            // core.quotepath=false：路径里有非 ASCII 时不转义
            Arguments = $"-c core.quotepath=false {arguments}",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return null;
        }

        var output = await SafeReadAsync(stdoutTask);
        await SafeReadAsync(stderrTask);

        if (process.ExitCode != 0)
        {
            return null;
        }

        // 只去尾部的空白：**不能**用 Trim()，它会把 porcelain 第一条记录的前导空格吃掉
        // （" M a.txt" → "M a.txt"，路径就从第 3 位错到第 2 位了）
        var trimmed = output.TrimEnd('\0', '\r', '\n', ' ');

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>跑一条命令并把内容写进它的标准输入（check-ignore --stdin 这类）。</summary>
    private async Task<string?> RunWithInputAsync(
        string workingDirectory,
        string arguments,
        string input,
        CancellationToken ct)
    {
        if (_gitPath is null || !Directory.Exists(workingDirectory))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _gitPath,
            Arguments = $"-c core.quotepath=false {arguments}",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return null;
        }

        var output = await SafeReadAsync(stdoutTask);
        await SafeReadAsync(stderrTask);

        if (process.ExitCode != 0)
        {
            return null;
        }

        var trimmed = output.TrimEnd('\0', '\r', '\n', ' ');

        return trimmed.Length == 0 ? null : trimmed;
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>路径可能是文件，取它所在的目录。</summary>
    private static string? ResolveDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                return path;
            }

            var directory = Path.GetDirectoryName(path);

            return Directory.Exists(directory) ? directory : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>在 PATH 与常见安装位置里找 git.exe。</summary>
    private static string? FindGitExecutable()
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");

        if (!string.IsNullOrEmpty(pathVariable))
        {
            foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), "git.exe");

                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }

        string[] commonPaths =
        [
            @"C:\Program Files\Git\cmd\git.exe",
            @"C:\Program Files\Git\bin\git.exe",
            @"C:\Program Files (x86)\Git\cmd\git.exe",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "Git",
                "cmd",
                "git.exe"),
        ];

        foreach (var candidate in commonPaths)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
