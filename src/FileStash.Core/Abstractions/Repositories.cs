using FileStash.Core.Models;

namespace FileStash.Core.Abstractions;

/// <summary>暂存组仓储：管理暂存组的持久化与生命周期。</summary>
public interface IStashGroupRepository
{
    /// <summary>仅当指定组为临时组且项计数为 0 时删除该组。</summary>
    Task<bool> DeleteEmptyTemporaryAsync(string id, CancellationToken ct);

    /// <summary>获取所有暂存组列表。</summary>
    Task<IReadOnlyList<StashGroup>> GetAllAsync(CancellationToken ct);

    /// <summary>根据组 ID 获取单个暂存组。</summary>
    Task<StashGroup?> GetAsync(string id, CancellationToken ct);

    /// <summary>新增暂存组记录。</summary>
    Task AddAsync(StashGroup group, CancellationToken ct);

    /// <summary>更新暂存组信息。</summary>
    Task UpdateAsync(StashGroup group, CancellationToken ct);

    /// <summary>根据 ID 删除非默认暂存组。</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct);

    /// <summary>清空所有暂存项并删除非默认组。</summary>
    Task<bool> ClearAllAsync(CancellationToken ct);
}

/// <summary>暂存项仓储：管理顶层暂存文件/文件夹元数据。</summary>
public interface IStashItemRepository
{
    /// <summary>在单一事务中保存暂存项与其展开快照条目。</summary>
    Task SaveSnapshotAsync(StashItem item, IReadOnlyList<StashEntry> entries, bool create, CancellationToken ct);

    /// <summary>获取指定组内的所有暂存项。</summary>
    Task<IReadOnlyList<StashItem>> GetByGroupAsync(string groupId, CancellationToken ct);

    /// <summary>根据 ID 列表批量获取暂存项。</summary>
    Task<IReadOnlyList<StashItem>> GetByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>按 SourcePathKey 查重（不用 SQLite 的 NOCASE，它只折叠 ASCII）。</summary>
    Task<StashItem?> GetBySourcePathAsync(string groupId, string sourcePathKey, CancellationToken ct);

    /// <summary>新增单个暂存项。</summary>
    Task AddAsync(StashItem item, CancellationToken ct);

    /// <summary>批量新增暂存项。</summary>
    Task AddManyAsync(IReadOnlyList<StashItem> items, CancellationToken ct);

    /// <summary>更新暂存项元数据。</summary>
    Task UpdateAsync(StashItem item, CancellationToken ct);

    /// <summary>删除单个暂存项。</summary>
    Task DeleteAsync(string id, CancellationToken ct);

    /// <summary>批量删除暂存项。</summary>
    Task DeleteManyAsync(IReadOnlyList<string> ids, CancellationToken ct);
}

/// <summary>快照条目仓储：管理暂存项展开后的具体文件/空目录快照。</summary>
public interface IStashEntryRepository
{
    /// <summary>获取指定暂存项包含的所有文件/目录条目。</summary>
    Task<IReadOnlyList<StashEntry>> GetByItemAsync(string itemId, CancellationToken ct);

    /// <summary>批量添加条目快照记录。</summary>
    Task AddManyAsync(IReadOnlyList<StashEntry> entries, CancellationToken ct);

    /// <summary>删除指定暂存项关联的所有条目。</summary>
    Task DeleteByItemAsync(string itemId, CancellationToken ct);

    /// <summary>所有被暂存项引用的 SHA-256。</summary>
    Task<IReadOnlySet<string>> GetReferencedSha256Async(CancellationToken ct);
}

/// <summary>根目录预设仓储：管理写入目标的常用基准根路径预设。</summary>
public interface IRootPresetRepository
{
    /// <summary>获取所有根目录预设。</summary>
    Task<IReadOnlyList<RootPreset>> GetAllAsync(CancellationToken ct);

    /// <summary>新增根目录预设。</summary>
    Task AddAsync(RootPreset preset, CancellationToken ct);

    /// <summary>更新根目录预设。</summary>
    Task UpdateAsync(RootPreset preset, CancellationToken ct);

    /// <summary>删除根目录预设。</summary>
    Task DeleteAsync(string id, CancellationToken ct);
}

/// <summary>写回批次操作仓储：记录 Apply 操作执行与恢复元数据。</summary>
public interface IOperationRepository
{
    /// <summary>记录新的写回批次操作。</summary>
    Task AddAsync(Operation operation, CancellationToken ct);

    /// <summary>更新批次操作状态及汇总结果。</summary>
    Task UpdateAsync(Operation operation, CancellationToken ct);

    /// <summary>根据 ID 获取操作记录。</summary>
    Task<Operation?> GetAsync(string id, CancellationToken ct);

    /// <summary>获取所有历史操作记录。</summary>
    Task<IReadOnlyList<Operation>> GetAllAsync(CancellationToken ct);

    /// <summary>早于给定时刻的操作（用于按保留天数清理历史）。</summary>
    Task<IReadOnlyList<Operation>> GetOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);

    /// <summary>删除单个历史操作。</summary>
    Task DeleteAsync(string id, CancellationToken ct);

    /// <summary>清理早于指定时间的历史操作。</summary>
    Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);

    /// <summary>Running operations and unresolved journals, excluding fully undone operations.</summary>
    Task<IReadOnlyList<string>> GetRecoveryCandidateIdsAsync(CancellationToken ct);
}

/// <summary>写回条目明细仓储：记录批次操作中每个目标文件的物理写入、备份与撤销事务日志。</summary>
public interface IOperationEntryRepository
{
    /// <summary>批量写入操作条目明细。</summary>
    Task AddManyAsync(IReadOnlyList<OperationEntry> entries, CancellationToken ct);

    /// <summary>获取指定操作对应的所有条目明细。</summary>
    Task<IReadOnlyList<OperationEntry>> GetByOperationAsync(string operationId, CancellationToken ct);

    /// <summary>更新条目执行状态（确认提交、撤销标记等）。</summary>
    Task UpdateAsync(OperationEntry entry, CancellationToken ct);

    /// <summary>所有仍有效的备份 SHA-256（用于"清理未引用数据"时保留备份）。</summary>
    Task<IReadOnlySet<string>> GetReferencedBackupSha256Async(CancellationToken ct);
}
