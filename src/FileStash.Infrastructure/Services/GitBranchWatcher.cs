using FileStash.Core.Abstractions;
using FileStash.Core.Utils;

namespace FileStash.Infrastructure.Services;

/// <summary>分支切换事件。</summary>
public sealed class BranchChangedEventArgs(string repoRoot, string? oldBranch, string? newBranch) : EventArgs
{
    public string RepoRoot { get; } = repoRoot;

    public string? OldBranch { get; } = oldBranch;

    public string? NewBranch { get; } = newBranch;
}

/// <summary>
/// 监听仓库的 HEAD 变化（切分支）。
/// </summary>
/// <remarks>
/// 监听的是 &lt;gitdir&gt;\HEAD 文件 —— 用 <c>GetGitDirAsync</c> 取，不能假定是
/// <c>&lt;repo&gt;\.git</c>：worktree 的 .git 是个文件，指向别处。
/// 变化后防抖 500ms 再读分支：git 切分支时会连续写好几次 HEAD。
/// </remarks>
public sealed class GitBranchWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly IGitService _git;
    private readonly Dictionary<string, WatchState> _watchers = new(StringComparer.OrdinalIgnoreCase);

    public GitBranchWatcher(IGitService git)
    {
        _git = git;
    }

    public event EventHandler<BranchChangedEventArgs>? BranchChanged;

    /// <summary>开始监听一个仓库。</summary>
    public async Task WatchAsync(string repoRoot, CancellationToken ct)
    {
        if (!_git.IsAvailable || _watchers.ContainsKey(repoRoot))
        {
            return;
        }

        var gitDir = await _git.GetGitDirAsync(repoRoot, ct);

        if (string.IsNullOrEmpty(gitDir) || !Directory.Exists(gitDir))
        {
            return;
        }

        var branch = await _git.GetCurrentBranchAsync(repoRoot, ct);

        var watcher = new FileSystemWatcher(gitDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            Filter = "HEAD",
            IncludeSubdirectories = false,
        };

        var state = new WatchState(repoRoot, branch, watcher);

        watcher.Changed += (_, _) => _ = OnChangedAsync(state);
        watcher.Created += (_, _) => _ = OnChangedAsync(state);
        watcher.Renamed += (_, _) => _ = OnChangedAsync(state);
        watcher.EnableRaisingEvents = true;

        _watchers[repoRoot] = state;
    }

    /// <summary>停止监听。</summary>
    public void Unwatch(string repoRoot)
    {
        if (_watchers.Remove(repoRoot, out var state))
        {
            state.Dispose();
        }
    }

    private async Task OnChangedAsync(WatchState state)
    {
        try
        {
            // 防抖：git 切分支时会连着写好几次 HEAD
            await Task.Delay(Debounce);

            var branch = await _git.GetCurrentBranchAsync(state.RepoRoot, CancellationToken.None);

            if (string.Equals(branch, state.Branch, StringComparison.Ordinal))
            {
                return;
            }

            var previous = state.Branch;
            state.Branch = branch;

            BranchChanged?.Invoke(this, new BranchChangedEventArgs(state.RepoRoot, previous, branch));
        }
        catch (Exception ex)
        {
            // 事件处理器是"发射后不管"的，异常会被静默吞掉 —— 记下来，否则排查时完全看不到线索
            LastError = ex;
        }
    }

    /// <summary>最近一次后台处理的异常（没有则为 null）。</summary>
    public Exception? LastError { get; private set; }

    public void Dispose()
    {
        foreach (var state in _watchers.Values)
        {
            state.Dispose();
        }

        _watchers.Clear();
    }

    private sealed class WatchState(string repoRoot, string? branch, FileSystemWatcher watcher) : IDisposable
    {
        public string RepoRoot { get; } = repoRoot;

        public string? Branch { get; set; } = branch;

        public FileSystemWatcher Watcher { get; } = watcher;

        public void Dispose()
        {
            Watcher.EnableRaisingEvents = false;
            Watcher.Dispose();
        }
    }
}
