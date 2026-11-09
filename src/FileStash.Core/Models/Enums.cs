namespace FileStash.Core.Models;

/// <summary>暂存项类型：文件或文件夹。</summary>
public enum StashItemKind
{
    File = 0,
    Folder = 1,
}

/// <summary>写入目标模式（原位置、其他根目录、指定路径）。</summary>
public enum TargetMode
{
    /// <summary>S1：原位置（Item.RootPath + 相对路径）。</summary>
    OriginalLocation = 0,

    /// <summary>S2：其他根目录。</summary>
    AnotherRoot = 1,

    /// <summary>S3：指定路径。</summary>
    SpecifiedPath = 2,

    /// <summary>S4：拖出（物化到 export 目录）。</summary>
    DragExport = 3,
}

/// <summary>写入时的目录结构保留方式。</summary>
public enum StructureMode
{
    /// <summary>保留从根起的完整子路径（默认）。</summary>
    FullRelative = 0,

    /// <summary>只保留暂存项自身结构（文件=文件名，文件夹=文件夹名\...）。</summary>
    ItemOnly = 1,

    /// <summary>所有文件直接放进目标目录。</summary>
    Flatten = 2,
}

/// <summary>目标已存在时的冲突处理策略。</summary>
public enum ConflictPolicy
{
    Ask,
    Overwrite,
    Skip,
    Rename,
}

/// <summary>计划阶段判定出的目标状态。</summary>
public enum PlannedState
{
    /// <summary>目标不存在。</summary>
    New,

    /// <summary>目标存在且内容不同。</summary>
    Overwrite,

    /// <summary>目标存在且哈希相同。</summary>
    Identical,

    /// <summary>目标路径是目录但要写文件（或反之）。</summary>
    TypeConflict,

    /// <summary>越界 / 非法路径。</summary>
    Invalid,
}

/// <summary>计划阶段的动作。</summary>
public enum PlannedAction
{
    Create,
    Overwrite,
    Skip,
    Rename,
}

/// <summary>单个条目的执行结果（同时用作 OperationEntry.Action）。</summary>
public enum EntryResult
{
    Created,
    Overwritten,
    Skipped,
    Renamed,
    Failed,
    DirectoryCreated,
}

/// <summary>一次"应用"操作的状态。</summary>
public enum OperationStatus
{
    Running = 0,
    Succeeded = 1,
    PartiallyFailed = 2,
    Failed = 3,
    Cancelled = 4,
    Undone = 5,
}

/// <summary>
/// 工作台的条目视图档位。
/// </summary>
/// <remarks>
/// <see cref="Tree"/> 必须是 0：老 settings.json 没有这个键，缺省落到 0，
/// 0 若不是树，升级后所有人的工作台会突然换一种看法。
/// </remarks>
public enum WorkbenchViewMode
{
    /// <summary>树：每个暂存项一个可展开节点，展开看它包含的条目。</summary>
    Tree = 0,

    /// <summary>列表：平铺暂存项，不展开条目。</summary>
    List = 1,

    /// <summary>按来源：以来源目录分节，节内列暂存项。</summary>
    BySourceFolder = 2,

    /// <summary>图标：紧凑网格。</summary>
    Icons = 3,
}
