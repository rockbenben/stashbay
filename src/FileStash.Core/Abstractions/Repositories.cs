using FileStash.Core.Models;

namespace FileStash.Core.Abstractions;

public interface IStashGroupRepository
{
    Task<IReadOnlyList<StashGroup>> GetAllAsync(CancellationToken ct);

    Task<StashGroup?> GetAsync(string id, CancellationToken ct);

    Task AddAsync(StashGroup group, CancellationToken ct);

    Task UpdateAsync(StashGroup group, CancellationToken ct);

    Task DeleteAsync(string id, CancellationToken ct);
}

public interface IStashItemRepository
{
    Task SaveSnapshotAsync(StashItem item, IReadOnlyList<StashEntry> entries, bool create, CancellationToken ct);

    Task<IReadOnlyList<StashItem>> GetByGroupAsync(string groupId, CancellationToken ct);

    Task<IReadOnlyList<StashItem>> GetByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct);

    /// <summary>按 SourcePathKey 查重（不用 SQLite 的 NOCASE，它只折叠 ASCII）。</summary>
    Task<StashItem?> GetBySourcePathAsync(string groupId, string sourcePathKey, CancellationToken ct);

    Task AddAsync(StashItem item, CancellationToken ct);

    Task AddManyAsync(IReadOnlyList<StashItem> items, CancellationToken ct);

    Task UpdateAsync(StashItem item, CancellationToken ct);

    Task DeleteAsync(string id, CancellationToken ct);

    Task DeleteManyAsync(IReadOnlyList<string> ids, CancellationToken ct);
}

public interface IStashEntryRepository
{
    Task<IReadOnlyList<StashEntry>> GetByItemAsync(string itemId, CancellationToken ct);

    Task AddManyAsync(IReadOnlyList<StashEntry> entries, CancellationToken ct);

    Task DeleteByItemAsync(string itemId, CancellationToken ct);

    /// <summary>所有被暂存项引用的 SHA-256。</summary>
    Task<IReadOnlySet<string>> GetReferencedSha256Async(CancellationToken ct);
}

public interface IRootPresetRepository
{
    Task<IReadOnlyList<RootPreset>> GetAllAsync(CancellationToken ct);

    Task AddAsync(RootPreset preset, CancellationToken ct);

    Task UpdateAsync(RootPreset preset, CancellationToken ct);

    Task DeleteAsync(string id, CancellationToken ct);
}

public interface IOperationRepository
{
    Task AddAsync(Operation operation, CancellationToken ct);

    Task UpdateAsync(Operation operation, CancellationToken ct);

    Task<Operation?> GetAsync(string id, CancellationToken ct);

    Task<IReadOnlyList<Operation>> GetAllAsync(CancellationToken ct);

    /// <summary>早于给定时刻的操作（用于按保留天数清理历史）。</summary>
    Task<IReadOnlyList<Operation>> GetOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);

    Task DeleteAsync(string id, CancellationToken ct);

    Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);
}

public interface IOperationEntryRepository
{
    Task AddManyAsync(IReadOnlyList<OperationEntry> entries, CancellationToken ct);

    Task<IReadOnlyList<OperationEntry>> GetByOperationAsync(string operationId, CancellationToken ct);

    Task UpdateAsync(OperationEntry entry, CancellationToken ct);

    /// <summary>所有仍有效的备份 SHA-256（用于"清理未引用数据"时保留备份）。</summary>
    Task<IReadOnlySet<string>> GetReferencedBackupSha256Async(CancellationToken ct);
}
