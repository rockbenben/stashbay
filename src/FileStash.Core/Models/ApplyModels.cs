using FileStash.Core.Resources;

namespace FileStash.Core.Models;

/// <summary>写入目标描述。</summary>
/// <param name="Mode">目标模式。</param>
/// <param name="TargetPath">
/// AnotherRoot：新根；SpecifiedPath：目标目录或完整文件名；DragExport：不用。
/// </param>
/// <param name="Structure">目录结构保留方式（对所有模式生效）。</param>
/// <param name="StripLeadingSegments">剥离前几段（只在 Structure=FullRelative 时有意义）。</param>
/// <param name="TreatTargetAsFileName">目标即完整文件名（仅当计划内只有 1 个文件时允许）。</param>
public sealed record TargetSpec(
    TargetMode Mode,
    string? TargetPath,
    StructureMode Structure,
    int StripLeadingSegments = 0,
    bool TreatTargetAsFileName = false);

/// <summary>计划里的一个条目。</summary>
public sealed class PlannedEntry
{
    public required StashEntry Source { get; init; }

    public required string TargetPath { get; set; }

    public PlannedState State { get; set; }

    /// <summary>用户可在预览中修改。</summary>
    public PlannedAction Action { get; set; }

    /// <summary>如"目标有未提交改动"、"只读文件"。</summary>
    public string? Warning { get; set; }

    /// <summary>所属的暂存项（预览界面显示用）。</summary>
    public string? ItemId { get; init; }

    /// <summary>所属暂存项的名字（预览界面显示用）。</summary>
    public string? ItemDisplayName { get; init; }

    /// <summary>
    /// 这一行要不要人当场决定：只有"会被覆盖"与"根本写不了"的行需要。
    /// 每个数据档都摆一颗下拉的话，24 行的表键盘要走 81 站，
    /// 而绝大多数行没有可决定的东西。
    /// </summary>
    public bool NeedsDecision => State is PlannedState.Overwrite
        or PlannedState.TypeConflict
        or PlannedState.Invalid;
}

/// <summary>一次应用的完整计划。</summary>
public sealed class ApplyPlan
{
    public required TargetSpec Target { get; init; }

    public required IReadOnlyList<PlannedEntry> Entries { get; init; }

    /// <summary>来源分支 vs 当前分支。</summary>
    public GitContext? GitContext { get; init; }

    /// <summary>计划里有问题的条目（TypeConflict / Invalid）。</summary>
    public IEnumerable<PlannedEntry> ProblemEntries =>
        Entries.Where(entry => entry.State is PlannedState.TypeConflict or PlannedState.Invalid);
}

/// <summary>Git 上下文：来源分支与当前分支。</summary>
public sealed record GitContext(string RepoRoot, string? SourceBranch, string? CurrentBranch)
{
    /// <summary>当前分支与来源分支不同（切过分支）。</summary>
    public bool BranchChanged =>
        SourceBranch is not null && CurrentBranch is not null &&
        !string.Equals(SourceBranch, CurrentBranch, StringComparison.Ordinal);

    /// <summary>预览顶部的提示文字。</summary>
    public string Describe() => BranchChanged
        ? Strings.Format(Strings.BranchChangedText, CurrentBranch, SourceBranch)
        : Strings.Format(Strings.StillOnSourceBranchText, CurrentBranch);
}

/// <summary>撤销结果。</summary>
public sealed class UndoResult
{
    public int Restored { get; init; }

    public int Deleted { get; init; }

    public int Skipped { get; init; }

    public int Failed { get; init; }

    public IReadOnlyList<string> Messages { get; init; } = [];

    public bool HasIssues => Skipped > 0 || Failed > 0;
}
