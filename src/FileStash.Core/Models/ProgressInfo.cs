namespace FileStash.Core.Models;

/// <summary>进度信息。</summary>
public sealed record ProgressInfo(int Done, int Total, string? CurrentPath);

/// <summary>源文件与暂存快照的对比结果。</summary>
public enum ItemSourceState
{
    /// <summary>与快照一致。</summary>
    Unchanged,

    /// <summary>内容有变化。</summary>
    Modified,

    /// <summary>源路径已不存在。</summary>
    Missing,
}

/// <summary>单个暂存项的源状态。</summary>
public sealed record ItemSourceStatus(string ItemId, string DisplayName, ItemSourceState State);

/// <summary>一次"加入暂存"的结果。</summary>
public sealed class AddResult
{
    /// <summary>成功加入的暂存项。</summary>
    public IReadOnlyList<StashItem> Added { get; init; } = [];

    /// <summary>同组里已存在相同源路径的路径（交给 UI 问"更新快照 / 跳过"）。</summary>
    public IReadOnlyList<string> Duplicates { get; init; } = [];

    /// <summary>跳过的符号链接、读不了的目录等。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>读取失败的文件。</summary>
    public IReadOnlyList<string> Failed { get; init; } = [];

    public bool HasIssues => Duplicates.Count > 0 || Warnings.Count > 0 || Failed.Count > 0;
}
