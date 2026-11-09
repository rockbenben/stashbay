namespace FileStash.Core.Models;

/// <summary>暂存组。</summary>
public sealed class StashGroup
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Note { get; set; }

    public bool IsPinned { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>一个暂存项：用户选择的一个文件或文件夹。</summary>
public sealed class StashItem
{
    public string Id { get; set; } = string.Empty;

    public string GroupId { get; set; } = string.Empty;

    public StashItemKind Kind { get; set; }

    /// <summary>文件/文件夹名，仅用于显示，不参与任何路径计算。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>加入时的绝对路径。</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>SourcePath 的不变式大写形式，仅用于唯一性判断与 SQLite 大小写不敏感比对。</summary>
    public string SourcePathKey { get; set; } = string.Empty;

    /// <summary>计算子路径的根，也是写入的唯一基准根。</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>SourcePath 相对 RootPath。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string? GitRepoRoot { get; set; }

    public string? GitBranch { get; set; }

    public string? GitCommit { get; set; }

    public int FileCount { get; set; }

    public long TotalSize { get; set; }

    public DateTimeOffset AddedAt { get; set; }

    /// <summary>最近一次快照时间。</summary>
    public DateTimeOffset SnapshotAt { get; set; }
}

/// <summary>暂存项内的每个文件 / 空目录。</summary>
public sealed class StashEntry
{
    public long Id { get; set; }

    public string ItemId { get; set; } = string.Empty;

    /// <summary>相对 Item.RootPath。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>仅用于记录空目录。</summary>
    public bool IsDirectory { get; set; }

    /// <summary>目录为 null。</summary>
    public string? Sha256 { get; set; }

    public long Size { get; set; }

    public DateTimeOffset? SourceLastWriteUtc { get; set; }
}

/// <summary>根目录预设。</summary>
public sealed class RootPreset
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}

/// <summary>一次"应用"操作。</summary>
public sealed class Operation
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? GroupId { get; set; }

    public TargetMode TargetMode { get; set; }

    /// <summary>人类可读的目标说明，如"原位置" / "D:\proj-b"。</summary>
    public string TargetDescription { get; set; } = string.Empty;

    public OperationStatus Status { get; set; }

    /// <summary>JSON: {created, overwritten, skipped, failed}。</summary>
    public string? Summary { get; set; }
}

/// <summary>操作内的单个条目。</summary>
public sealed class OperationEntry
{
    public long Id { get; set; }

    public string OperationId { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public EntryResult Action { get; set; }
    public PlannedAction? IntendedAction { get; set; }
    /// <summary>Intent was saved, but file commit has not been acknowledged.</summary>
    public bool Pending { get; set; }
    public string? WitnessPath { get; set; }
    public string? WitnessIdentity { get; set; }
    public string? PreviousSha256 { get; set; }
    public bool UndoPending { get; set; }
    public bool Undone { get; set; }

    /// <summary>写入的内容。</summary>
    public string? NewSha256 { get; set; }

    /// <summary>被覆盖前的内容（用于撤销）。</summary>
    public string? BackupSha256 { get; set; }

    /// <summary>本次是否新建了该目录。</summary>
    public bool CreatedDirectory { get; set; }

    public string? Error { get; set; }
}
