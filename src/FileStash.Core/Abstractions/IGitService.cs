using FileStash.Core.Models;

namespace FileStash.Core.Abstractions;

/// <summary>
/// Git 服务。
/// </summary>
/// <remarks>
/// M2 只需要仓库根 / 当前分支 / HEAD 提交；M7 补上 status、check-ignore、add、git-dir
/// （接口是逐步长出来的，已有调用方不受影响）。
/// </remarks>
public interface IGitService
{
    /// <summary>系统里找不找得到 git。找不到时所有 Git 功能自动降级，不报错。</summary>
    bool IsAvailable { get; }

    /// <summary>路径所在仓库的根目录；不在仓库里返回 null。</summary>
    Task<string?> GetRepoRootAsync(string path, CancellationToken ct);

    /// <summary>当前分支名；detached HEAD 时返回短提交号。</summary>
    Task<string?> GetCurrentBranchAsync(string repoRoot, CancellationToken ct);

    /// <summary>HEAD 的完整提交号。</summary>
    Task<string?> GetHeadCommitAsync(string repoRoot, CancellationToken ct);

    /// <summary>工作区状态（porcelain v1）。</summary>
    Task<IReadOnlyList<GitStatusEntry>> GetStatusAsync(string repoRoot, CancellationToken ct);

    /// <summary>哪些路径被 .gitignore 忽略了。</summary>
    Task<IReadOnlySet<string>> FilterIgnoredAsync(string repoRoot, IReadOnlyList<string> relativePaths, CancellationToken ct);

    /// <summary>把路径加入暂存区（git add）。</summary>
    Task AddAsync(string repoRoot, IReadOnlyList<string> relativePaths, CancellationToken ct);

    /// <summary>仓库的 .git 目录（支持 worktree：worktree 的 .git 是个文件，指向别处）。</summary>
    Task<string?> GetGitDirAsync(string repoRoot, CancellationToken ct);
}
