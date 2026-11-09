using Dapper;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Utils;
using Microsoft.Data.Sqlite;

namespace FileStash.Infrastructure.Data;

public sealed class StashGroupRepository : IStashGroupRepository
{
    private const string SelectColumns = "Id, Name, Note, IsPinned, SortOrder, CreatedAt, UpdatedAt";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public StashGroupRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<StashGroup>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        var groups = await connection.QueryAsync<StashGroup>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM StashGroup ORDER BY IsPinned DESC, SortOrder, Name;",
            cancellationToken: ct));

        return groups.AsList();
    }

    public async Task<StashGroup?> GetAsync(string id, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        var group = await connection.QuerySingleOrDefaultAsync<StashGroup>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM StashGroup WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: ct));

        return group;
    }

    public async Task AddAsync(StashGroup group, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO StashGroup (Id, Name, Note, IsPinned, SortOrder, CreatedAt, UpdatedAt)
            VALUES (@Id, @Name, @Note, @IsPinned, @SortOrder, @CreatedAt, @UpdatedAt);
            """,
            group,
            cancellationToken: ct));
    }

    public async Task UpdateAsync(StashGroup group, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE StashGroup
               SET Name = @Name, Note = @Note, IsPinned = @IsPinned,
                   SortOrder = @SortOrder, UpdatedAt = @UpdatedAt
             WHERE Id = @Id;
            """,
            group,
            cancellationToken: ct));
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        // StashItem 上的外键是 ON DELETE CASCADE，组删了项跟着删
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM StashGroup WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: ct));
    }
}

public sealed class StashItemRepository : IStashItemRepository
{
    private const string SelectColumns =
        "Id, GroupId, Kind, DisplayName, SourcePath, SourcePathKey, RootPath, RelativePath, " +
        "GitRepoRoot, GitBranch, GitCommit, FileCount, TotalSize, AddedAt, SnapshotAt";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public StashItemRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<StashItem>> GetByGroupAsync(string groupId, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        var items = await connection.QueryAsync<StashItem>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM StashItem WHERE GroupId = @GroupId ORDER BY AddedAt;",
            new { GroupId = groupId },
            cancellationToken: ct));

        return items.AsList();
    }

    public async Task<IReadOnlyList<StashItem>> GetByIdsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        await using var connection = await _connectionFactory.OpenAsync(ct);

        var items = await connection.QueryAsync<StashItem>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM StashItem WHERE Id IN @Ids;",
            new { Ids = ids },
            cancellationToken: ct));

        return items.AsList();
    }

    public async Task<StashItem?> GetBySourcePathAsync(string groupId, string sourcePathKey, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        // 归一化是幂等的：传进来的既可以是路径，也可以是已经归一化的键
        var key = PathUtil.ToKey(sourcePathKey);

        var item = await connection.QuerySingleOrDefaultAsync<StashItem>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM StashItem WHERE GroupId = @GroupId AND SourcePathKey = @SourcePathKey;",
            new { GroupId = groupId, SourcePathKey = key },
            cancellationToken: ct));

        return item;
    }

    public async Task AddAsync(StashItem item, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO StashItem (Id, GroupId, Kind, DisplayName, SourcePath, SourcePathKey, RootPath,
                                   RelativePath, GitRepoRoot, GitBranch, GitCommit, FileCount, TotalSize,
                                   AddedAt, SnapshotAt)
            VALUES (@Id, @GroupId, @Kind, @DisplayName, @SourcePath, @SourcePathKey, @RootPath,
                    @RelativePath, @GitRepoRoot, @GitBranch, @GitCommit, @FileCount, @TotalSize,
                    @AddedAt, @SnapshotAt);
            """,
            ToRow(item),
            cancellationToken: ct));
    }

    public async Task AddManyAsync(IReadOnlyList<StashItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO StashItem (Id, GroupId, Kind, DisplayName, SourcePath, SourcePathKey, RootPath,
                                   RelativePath, GitRepoRoot, GitBranch, GitCommit, FileCount, TotalSize,
                                   AddedAt, SnapshotAt)
            VALUES (@Id, @GroupId, @Kind, @DisplayName, @SourcePath, @SourcePathKey, @RootPath,
                    @RelativePath, @GitRepoRoot, @GitBranch, @GitCommit, @FileCount, @TotalSize,
                    @AddedAt, @SnapshotAt);
            """,
            items.Select(ToRow).ToList(),
            transaction,
            cancellationToken: ct));

        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// 写入用的行：SourcePathKey 一律由 SourcePath 归一化得出。
    /// </summary>
    /// <remarks>
    /// 唯一性判断依赖这个键，而 SQLite 的 TEXT 比较默认是大小写敏感的（BINARY 排序）。
    /// 所以键的归一化不能靠调用方自觉 —— 由仓储层统一算，调用方传什么形式的路径都安全。
    /// </remarks>
    private static object ToRow(StashItem item) => new
    {
        item.Id,
        item.GroupId,
        item.Kind,
        item.DisplayName,
        item.SourcePath,
        SourcePathKey = PathUtil.ToKey(item.SourcePath),
        item.RootPath,
        item.RelativePath,
        item.GitRepoRoot,
        item.GitBranch,
        item.GitCommit,
        item.FileCount,
        item.TotalSize,
        item.AddedAt,
        item.SnapshotAt,
    };

    public async Task UpdateAsync(StashItem item, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE StashItem
               SET GroupId = @GroupId, Kind = @Kind, DisplayName = @DisplayName, SourcePath = @SourcePath,
                   SourcePathKey = @SourcePathKey, RootPath = @RootPath, RelativePath = @RelativePath,
                   GitRepoRoot = @GitRepoRoot, GitBranch = @GitBranch, GitCommit = @GitCommit,
                   FileCount = @FileCount, TotalSize = @TotalSize, SnapshotAt = @SnapshotAt
             WHERE Id = @Id;
            """,
            ToRow(item),
            cancellationToken: ct));
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM StashItem WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: ct));
    }

    public async Task DeleteManyAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM StashItem WHERE Id IN @Ids;",
            new { Ids = ids },
            cancellationToken: ct));
    }
}

public sealed class StashEntryRepository : IStashEntryRepository
{
    private const string SelectColumns = "Id, ItemId, RelativePath, IsDirectory, Sha256, Size, SourceLastWriteUtc";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public StashEntryRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<StashEntry>> GetByItemAsync(string itemId, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        var entries = await connection.QueryAsync<StashEntry>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM StashEntry WHERE ItemId = @ItemId ORDER BY RelativePath;",
            new { ItemId = itemId },
            cancellationToken: ct));

        return entries.AsList();
    }

    public async Task AddManyAsync(IReadOnlyList<StashEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO StashEntry (ItemId, RelativePath, IsDirectory, Sha256, Size, SourceLastWriteUtc)
            VALUES (@ItemId, @RelativePath, @IsDirectory, @Sha256, @Size, @SourceLastWriteUtc);
            """,
            entries,
            transaction,
            cancellationToken: ct));

        await transaction.CommitAsync(ct);
    }

    public async Task DeleteByItemAsync(string itemId, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM StashEntry WHERE ItemId = @ItemId;",
            new { ItemId = itemId },
            cancellationToken: ct));
    }

    public async Task<IReadOnlySet<string>> GetReferencedSha256Async(CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        var hashes = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT Sha256 FROM StashEntry WHERE Sha256 IS NOT NULL;",
            cancellationToken: ct));

        return hashes.AsList().ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class RootPresetRepository : IRootPresetRepository
{
    private const string SelectColumns = "Id, Name, Path, SortOrder";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public RootPresetRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<RootPreset>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        var presets = await connection.QueryAsync<RootPreset>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM RootPreset ORDER BY SortOrder, Name;",
            cancellationToken: ct));

        return presets.AsList();
    }

    public async Task AddAsync(RootPreset preset, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO RootPreset (Id, Name, Path, SortOrder) VALUES (@Id, @Name, @Path, @SortOrder);",
            preset,
            cancellationToken: ct));
    }

    public async Task UpdateAsync(RootPreset preset, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE RootPreset SET Name = @Name, Path = @Path, SortOrder = @SortOrder WHERE Id = @Id;",
            preset,
            cancellationToken: ct));
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM RootPreset WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: ct));
    }
}

public sealed class OperationRepository : IOperationRepository
{
    private const string SelectColumns =
        "Id, StartedAt, FinishedAt, GroupId, TargetMode, TargetDescription, Status, Summary";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public OperationRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task AddAsync(Operation operation, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Operation (Id, StartedAt, FinishedAt, GroupId, TargetMode, TargetDescription, Status, Summary)
            VALUES (@Id, @StartedAt, @FinishedAt, @GroupId, @TargetMode, @TargetDescription, @Status, @Summary);
            """,
            operation,
            cancellationToken: ct));
    }

    public async Task UpdateAsync(Operation operation, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE Operation
               SET FinishedAt = @FinishedAt, GroupId = @GroupId, TargetMode = @TargetMode,
                   TargetDescription = @TargetDescription, Status = @Status, Summary = @Summary
             WHERE Id = @Id;
            """,
            operation,
            cancellationToken: ct));
    }

    public async Task<Operation?> GetAsync(string id, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        var operation = await connection.QuerySingleOrDefaultAsync<Operation>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM Operation WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: ct));

        return operation;
    }

    public async Task<IReadOnlyList<Operation>> GetAllAsync(CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        var operations = await connection.QueryAsync<Operation>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM Operation ORDER BY StartedAt DESC;",
            cancellationToken: ct));

        return operations.AsList();
    }

    public async Task<IReadOnlyList<Operation>> GetOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        var operations = await connection.QueryAsync<Operation>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM Operation WHERE StartedAt < @Cutoff ORDER BY StartedAt;",
            new { Cutoff = cutoff },
            cancellationToken: ct));

        return operations.AsList();
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Operation WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: ct));
    }

    public async Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Operation WHERE StartedAt < @Cutoff;",
            new { Cutoff = cutoff },
            cancellationToken: ct));
    }
}

public sealed class OperationEntryRepository : IOperationEntryRepository
{
    private const string SelectColumns =
        "Id, OperationId, TargetPath, Action, NewSha256, BackupSha256, CreatedDirectory, Error";

    private readonly ISqliteConnectionFactory _connectionFactory;

    public OperationEntryRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task AddManyAsync(IReadOnlyList<OperationEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await using var connection = await _connectionFactory.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO OperationEntry (OperationId, TargetPath, Action, NewSha256, BackupSha256, CreatedDirectory, Error)
            VALUES (@OperationId, @TargetPath, @Action, @NewSha256, @BackupSha256, @CreatedDirectory, @Error);
            """,
            entries,
            transaction,
            cancellationToken: ct));

        await transaction.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<OperationEntry>> GetByOperationAsync(string operationId, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        var entries = await connection.QueryAsync<OperationEntry>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM OperationEntry WHERE OperationId = @OperationId ORDER BY Id;",
            new { OperationId = operationId },
            cancellationToken: ct));

        return entries.AsList();
    }

    public async Task UpdateAsync(OperationEntry entry, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE OperationEntry
               SET TargetPath = @TargetPath, Action = @Action, NewSha256 = @NewSha256,
                   BackupSha256 = @BackupSha256, CreatedDirectory = @CreatedDirectory, Error = @Error
             WHERE Id = @Id;
            """,
            entry,
            cancellationToken: ct));
    }

    public async Task<IReadOnlySet<string>> GetReferencedBackupSha256Async(CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        var hashes = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT BackupSha256 FROM OperationEntry WHERE BackupSha256 IS NOT NULL;",
            cancellationToken: ct));

        return hashes.AsList().ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
