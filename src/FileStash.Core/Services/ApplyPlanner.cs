using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Resources;
using FileStash.Core.Utils;

namespace FileStash.Core.Services;

/// <summary>
/// 生成写入计划。纯计算，不动目标文件。
/// </summary>
public sealed class ApplyPlanner : IApplyPlanner
{
    private readonly IStashEntryRepository _entries;
    private readonly IGitService _git;
    private readonly PathMapper _mapper = new();

    public ApplyPlanner(IStashEntryRepository entries, IGitService git)
    {
        _entries = entries;
        _git = git;
    }

    public async Task<ApplyPlan> BuildPlanAsync(
        IReadOnlyList<StashItem> items,
        TargetSpec target,
        ConflictPolicy policy,
        CancellationToken ct,
        bool skipIdentical = true)
    {
        // 1. 收集所有条目
        var sources = new List<(StashItem Item, StashEntry Entry)>();

        foreach (var item in items)
        {
            var entries = await _entries.GetByItemAsync(item.Id, ct);

            foreach (var entry in entries)
            {
                sources.Add((item, entry));
            }
        }

        // 2. 计划内只有一个文件时，才允许"目标即文件名"
        var fileCount = sources.Count(pair => !pair.Entry.IsDirectory);
        var context = new PathMapper.MappingContext { SingleFile = fileCount == 1 };
        var treatAsFileNameNotAllowed = target.TreatTargetAsFileName && fileCount > 1;

        // 3. 有未提交改动的目标文件（提前查一次，git status 不便宜）
        var dirtyPaths = await GetDirtyPathsAsync(items, ct);

        // 4. 逐条映射并判定状态
        var planned = new List<PlannedEntry>();

        foreach (var (item, entry) in sources)
        {
            ct.ThrowIfCancellationRequested();

            var targetPath = _mapper.Map(item, entry, target, context);
            var state = PlannedState.New;

            if (treatAsFileNameNotAllowed)
            {
                // "目标即文件名"只在单文件时可用
                state = PlannedState.Invalid;
            }
            else if (IsOutOfBounds(item, entry, target, targetPath))
            {
                state = PlannedState.Invalid;
            }
            else
            {
                state = await DetermineStateAsync(targetPath, entry, ct);
            }

            var action = DetermineAction(state, policy, skipIdentical);
            var plannedEntry = new PlannedEntry
            {
                Source = entry,
                TargetPath = targetPath,
                State = state,
                Action = action,
                Warning = BuildWarning(targetPath, state),
                ItemId = item.Id,
                ItemDisplayName = item.DisplayName,
            };

            if (treatAsFileNameNotAllowed)
            {
                plannedEntry.Warning = Strings.TargetIsFileNameWarning;
            }
            else if (state == PlannedState.Overwrite && IsDirty(item, targetPath, dirtyPaths))
            {
                plannedEntry.Warning = Strings.UncommittedChangesWarning;
            }

            // Rename：目标名递增到不存在的那个
            if (action == PlannedAction.Rename)
            {
                plannedEntry.TargetPath = NextAvailableName(targetPath);
                plannedEntry.Warning = Strings.Format(Strings.RenameTargetWarning, Path.GetFileName(plannedEntry.TargetPath));
            }

            planned.Add(plannedEntry);
        }

        // 4. 同一目标出现两次：后者标为 Invalid
        MarkDuplicateTargets(planned);

        // 5. Git 上下文
        var gitContext = await BuildGitContextAsync(items, target, ct);

        return new ApplyPlan
        {
            Target = target,
            Entries = planned,
            GitContext = gitContext,
        };
    }

    /// <summary>取仓库里有未提交改动的路径（相对仓库根）。</summary>
    private async Task<HashSet<string>> GetDirtyPathsAsync(IReadOnlyList<StashItem> items, CancellationToken ct)
    {
        var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!_git.IsAvailable)
        {
            return dirty;
        }

        foreach (var repoRoot in items
                     .Select(item => item.GitRepoRoot)
                     .Where(root => !string.IsNullOrEmpty(root))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var status = await _git.GetStatusAsync(repoRoot!, ct);

            foreach (var entry in status)
            {
                if (entry.IsModified || entry.IsUntracked)
                {
                    dirty.Add(entry.Path);
                }
            }
        }

        return dirty;
    }

    /// <summary>目标文件是不是仓库里有未提交改动的那个。</summary>
    private static bool IsDirty(StashItem item, string targetPath, HashSet<string> dirtyPaths)
    {
        if (item.GitRepoRoot is null || dirtyPaths.Count == 0)
        {
            return false;
        }

        if (!PathUtil.IsUnder(item.GitRepoRoot, targetPath))
        {
            return false;
        }

        var relative = PathUtil.GetRelative(item.GitRepoRoot, targetPath);

        return dirtyPaths.Contains(relative);
    }

    /// <summary>越过界检查：算出来的目标必须在目标根之下；剥离段数不能超过子路径段数。</summary>
    private static bool IsOutOfBounds(StashItem item, StashEntry entry, TargetSpec target, string targetPath)
    {
        if (target.Mode == TargetMode.OriginalLocation)
        {
            // 原位置：相对 RootPath 的路径拼出来的，天然在其下
            return false;
        }

        if (target.Mode == TargetMode.DragExport)
        {
            return false;
        }

        // 剥离段数超过子路径段数 → Invalid
        if (target.Structure == StructureMode.FullRelative && target.StripLeadingSegments > 0)
        {
            var segments = PathUtil.SplitSegments(entry.RelativePath);

            if (segments.Length <= target.StripLeadingSegments)
            {
                return true;
            }
        }

        var root = PathMapper.ExpandTargetPath(target.TargetPath);

        if (string.IsNullOrEmpty(root))
        {
            return true;
        }

        return !PathUtil.IsUnder(root, targetPath);
    }

    private static async Task<PlannedState> DetermineStateAsync(string targetPath, StashEntry entry, CancellationToken ct)
    {
        if (Directory.Exists(targetPath))
        {
            // 目标是目录：写目录没问题，写文件就是类型冲突
            return entry.IsDirectory ? PlannedState.New : PlannedState.TypeConflict;
        }

        if (!File.Exists(targetPath))
        {
            return PlannedState.New;
        }

        var info = new FileInfo(targetPath);

        if (info.Length != entry.Size)
        {
            return PlannedState.Overwrite;
        }

        // 大小相同才算哈希
        string sha256;

        try
        {
            sha256 = await HashUtil.ComputeFileSha256Async(targetPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不了 —— 多半是被别的程序独占锁着（正开在 Excel 里）。
            // 这里不能把异常抛出去：计划阶段一炸，整个写回就没了，
            // 用户看到的是一个"出错了"的弹窗，而不是"这个文件正被谁占着"。
            // 读不了就没法确认是不是同一份，按"要覆盖"算；
            // 到底写不写得进去由执行阶段说了算，那时会把占用者的名字报出来。
            return PlannedState.Overwrite;
        }

        return string.Equals(sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase)
            ? PlannedState.Identical
            : PlannedState.Overwrite;
    }

    private static PlannedAction DetermineAction(PlannedState state, ConflictPolicy policy, bool skipIdentical) => state switch
    {
        PlannedState.New => PlannedAction.Create,
        // 不跳过相同时按覆盖走：内容一样也重写一遍（用户勾掉它就是想要"每次都落一遍"）
        PlannedState.Identical => skipIdentical ? PlannedAction.Skip : PlannedAction.Overwrite,

        PlannedState.Overwrite => policy switch
        {
            ConflictPolicy.Overwrite => PlannedAction.Overwrite,
            ConflictPolicy.Skip => PlannedAction.Skip,
            ConflictPolicy.Rename => PlannedAction.Rename,

            // Ask：先标为 Overwrite，预览里高亮，等用户确认
            _ => PlannedAction.Overwrite,
        },

        // 类型冲突与越界只能跳过，不允许改成覆盖
        _ => PlannedAction.Skip,
    };

    private static string? BuildWarning(string targetPath, PlannedState state)
    {
        if (state is not (PlannedState.Overwrite or PlannedState.TypeConflict))
        {
            return null;
        }

        if (!File.Exists(targetPath))
        {
            return null;
        }

        try
        {
            var attributes = File.GetAttributes(targetPath);

            return attributes.HasFlag(FileAttributes.ReadOnly) ? Strings.TargetReadOnly : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>同一目标出现两次：后者标为 Invalid。</summary>
    private static void MarkDuplicateTargets(List<PlannedEntry> planned)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in planned)
        {
            if (!seen.Add(entry.TargetPath))
            {
                entry.State = PlannedState.Invalid;
                entry.Action = PlannedAction.Skip;
                entry.Warning = Strings.DuplicateTargetWarning;
            }
        }
    }

    private async Task<GitContext?> BuildGitContextAsync(
        IReadOnlyList<StashItem> items,
        TargetSpec target,
        CancellationToken ct)
    {
        if (target.Mode != TargetMode.OriginalLocation)
        {
            return null;
        }

        var repoRoot = items
            .Select(item => item.GitRepoRoot)
            .FirstOrDefault(root => !string.IsNullOrEmpty(root));

        if (repoRoot is null || !_git.IsAvailable)
        {
            return null;
        }

        var currentBranch = await _git.GetCurrentBranchAsync(repoRoot, ct);
        var sourceBranch = items
            .Select(item => item.GitBranch)
            .FirstOrDefault(branch => !string.IsNullOrEmpty(branch));

        return new GitContext(repoRoot, sourceBranch, currentBranch);
    }

    /// <summary>name (2).ext、name (3).ext …… 直到那个名字不存在。</summary>
    private static string NextAvailableName(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");

            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
