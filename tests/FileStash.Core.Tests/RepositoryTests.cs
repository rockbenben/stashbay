using Dapper;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Infrastructure.Data;

namespace FileStash.Core.Tests;

public sealed class RepositoryTests : IAsyncLifetime
{
    private readonly TempWorkspace _workspace = new();

    private DatabaseMigrator _migrator = null!;
    private IStashGroupRepository _groups = null!;
    private IStashItemRepository _items = null!;
    private IStashEntryRepository _entries = null!;
    private IRootPresetRepository _presets = null!;
    private IOperationRepository _operations = null!;
    private IOperationEntryRepository _operationEntries = null!;

    public async ValueTask InitializeAsync()
    {
        _migrator = new DatabaseMigrator(_workspace.ConnectionFactory);
        await _migrator.MigrateAsync(TestContext.Current.CancellationToken);

        _groups = new StashGroupRepository(_workspace.ConnectionFactory);
        _items = new StashItemRepository(_workspace.ConnectionFactory);
        _entries = new StashEntryRepository(_workspace.ConnectionFactory);
        _presets = new RootPresetRepository(_workspace.ConnectionFactory);
        _operations = new OperationRepository(_workspace.ConnectionFactory);
        _operationEntries = new OperationEntryRepository(_workspace.ConnectionFactory);
    }

    public ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        return ValueTask.CompletedTask;
    }

    private static StashGroup NewGroup(string name = "默认") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static StashItem NewItem(string groupId, string sourcePath, string sourcePathKey) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        GroupId = groupId,
        Kind = StashItemKind.File,
        DisplayName = Path.GetFileName(sourcePath),
        SourcePath = sourcePath,
        SourcePathKey = sourcePathKey,
        RootPath = @"C:\repo",
        RelativePath = Path.GetFileName(sourcePath),
        FileCount = 1,
        TotalSize = 10,
        AddedAt = DateTimeOffset.UtcNow,
        SnapshotAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task MigrateAsync_ShouldBeIdempotent()
    {
        Assert.Equal(Migrations.All.Max(migration => migration.Version), await _migrator.MigrateAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Migrations.All.Max(migration => migration.Version), await _migrator.MigrateAsync(TestContext.Current.CancellationToken));

        var versions = await _migrator.GetAppliedVersionsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Migrations.All.Select(migration => migration.Version), versions);
    }

    [Fact]
    public async Task EnsureReadyAsync_ShouldMigrateOnlyOnce()
    {
        var migrator = new DatabaseMigrator(_workspace.ConnectionFactory);

        Assert.Equal(Migrations.All.Max(migration => migration.Version), await migrator.EnsureReadyAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Migrations.All.Max(migration => migration.Version), await migrator.EnsureReadyAsync(TestContext.Current.CancellationToken));

        var versions = await migrator.GetAppliedVersionsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Migrations.All.Select(migration => migration.Version), versions);
    }

    [Fact]
    public async Task Repository_ShouldWaitForSchemaOnFreshDatabase()
    {
        // 回归：干净环境里第一次运行时，主窗口一 Loaded 就会查表，
        // 而迁移被排在启动后期 —— 早于迁移的查询会抛 "no such table: StashGroup"，
        // 用户看到的是一个模态错误框，而且那个框会把本该跑起来的迁移一起卡死。
        // 门闩把"查询早于迁移"变成"查询等迁移"。
        var migrator = new DatabaseMigrator(_workspace.ConnectionFactory);
        var ready = new SchemaReadyConnectionFactory(_workspace.ConnectionFactory, migrator);
        var groups = new StashGroupRepository(ready);

        var all = await groups.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Empty(all);
    }

    [Fact]
    public async Task MigrateAsync_ShouldCreateAllTables()
    {
        await using var connection = await _workspace.ConnectionFactory.OpenAsync(TestContext.Current.CancellationToken);

        var tables = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM sqlite_master WHERE type = 'table';"))).AsList();

        Assert.Contains("StashGroup", tables);
        Assert.Contains("StashItem", tables);
        Assert.Contains("StashEntry", tables);
        Assert.Contains("RootPreset", tables);
        Assert.Contains("Operation", tables);
        Assert.Contains("OperationEntry", tables);
        Assert.Contains("SchemaVersion", tables);
    }

    [Fact]
    public async Task GroupRepository_ShouldRoundTrip()
    {
        var group = NewGroup("登录修改");
        await _groups.AddAsync(group, TestContext.Current.CancellationToken);

        var loaded = await _groups.GetAsync(group.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal("登录修改", loaded.Name);

        loaded.Name = "改过名";
        await _groups.UpdateAsync(loaded, TestContext.Current.CancellationToken);

        var reloaded = await _groups.GetAsync(group.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        Assert.Equal("改过名", reloaded.Name);

        await _groups.DeleteAsync(group.Id, TestContext.Current.CancellationToken);
        Assert.Null(await _groups.GetAsync(group.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GroupRepository_ShouldOrderPinnedFirst()
    {
        var normal = NewGroup("普通");
        var pinned = NewGroup("置顶");
        pinned.IsPinned = true;

        await _groups.AddAsync(normal, TestContext.Current.CancellationToken);
        await _groups.AddAsync(pinned, TestContext.Current.CancellationToken);

        var all = await _groups.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(pinned.Id, all[0].Id);
    }

    [Fact]
    public async Task ItemRepository_ShouldRejectDuplicateSourcePathInSameGroup()
    {
        var group = NewGroup();
        await _groups.AddAsync(group, TestContext.Current.CancellationToken);

        var first = NewItem(group.Id, @"C:\repo\a.txt", @"C:\REPO\A.TXT");
        await _items.AddAsync(first, TestContext.Current.CancellationToken);

        // 同一个源路径换大小写：应该被唯一索引挡住
        var duplicate = NewItem(group.Id, @"C:\repo\a.txt", @"C:\REPO\A.TXT");

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => _items.AddAsync(duplicate, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ItemRepository_ShouldFindBySourcePathKeyIgnoringCase()
    {
        var group = NewGroup();
        await _groups.AddAsync(group, TestContext.Current.CancellationToken);

        var item = NewItem(group.Id, @"C:\repo\a.txt", @"C:\REPO\A.TXT");
        await _items.AddAsync(item, TestContext.Current.CancellationToken);

        var found = await _items.GetBySourcePathAsync(group.Id, @"c:\repo\a.txt", TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal(item.Id, found.Id);
    }

    [Fact]
    public async Task ItemRepository_ShouldCascadeDeleteWithGroup()
    {
        var group = NewGroup();
        await _groups.AddAsync(group, TestContext.Current.CancellationToken);

        var item = NewItem(group.Id, @"C:\repo\a.txt", @"C:\REPO\A.TXT");
        await _items.AddAsync(item, TestContext.Current.CancellationToken);

        await _entries.AddManyAsync(
        [
            new StashEntry { ItemId = item.Id, RelativePath = "a.txt", Sha256 = new string('a', 64), Size = 10 },
        ], TestContext.Current.CancellationToken);

        await _groups.DeleteAsync(group.Id, TestContext.Current.CancellationToken);

        Assert.Empty(await _items.GetByGroupAsync(group.Id, TestContext.Current.CancellationToken));
        Assert.Empty(await _entries.GetByItemAsync(item.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EntryRepository_ShouldCollectReferencedHashes()
    {
        var group = NewGroup();
        await _groups.AddAsync(group, TestContext.Current.CancellationToken);

        var item = NewItem(group.Id, @"C:\repo\a.txt", @"C:\REPO\A.TXT");
        await _items.AddAsync(item, TestContext.Current.CancellationToken);

        var hash = new string('b', 64);
        await _entries.AddManyAsync(
        [
            new StashEntry { ItemId = item.Id, RelativePath = "a.txt", Sha256 = hash, Size = 10 },
            new StashEntry { ItemId = item.Id, RelativePath = "empty", IsDirectory = true },
        ], TestContext.Current.CancellationToken);

        var referenced = await _entries.GetReferencedSha256Async(TestContext.Current.CancellationToken);

        Assert.Equal([hash], referenced);
    }

    [Fact]
    public async Task OperationRepository_ShouldStoreEntriesAndBackupHashes()
    {
        var operation = new Operation
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.UtcNow,
            TargetMode = TargetMode.OriginalLocation,
            TargetDescription = "原位置",
            Status = OperationStatus.Running,
        };

        await _operations.AddAsync(operation, TestContext.Current.CancellationToken);

        var backup = new string('c', 64);
        await _operationEntries.AddManyAsync(
        [
            new OperationEntry
            {
                OperationId = operation.Id,
                TargetPath = @"C:\repo\a.txt",
                Action = EntryResult.Overwritten,
                NewSha256 = new string('d', 64),
                BackupSha256 = backup,
            },
        ], TestContext.Current.CancellationToken);

        var entries = await _operationEntries.GetByOperationAsync(operation.Id, TestContext.Current.CancellationToken);
        Assert.Single(entries);
        Assert.Equal(EntryResult.Overwritten, entries[0].Action);
        Assert.Equal(backup, entries[0].BackupSha256);

        var referenced = await _operationEntries.GetReferencedBackupSha256Async(TestContext.Current.CancellationToken);
        Assert.Equal([backup], referenced);

        operation.Status = OperationStatus.Succeeded;
        operation.FinishedAt = DateTimeOffset.UtcNow;
        await _operations.UpdateAsync(operation, TestContext.Current.CancellationToken);

        var reloaded = await _operations.GetAsync(operation.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded);
        Assert.Equal(OperationStatus.Succeeded, reloaded.Status);
        Assert.NotNull(reloaded.FinishedAt);
    }

    [Fact]
    public async Task OperationRepository_ShouldDeleteOlderThanCutoff()
    {
        var old = new Operation
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.UtcNow.AddDays(-40),
            TargetMode = TargetMode.OriginalLocation,
            TargetDescription = "原位置",
            Status = OperationStatus.Succeeded,
        };

        await _operations.AddAsync(old, TestContext.Current.CancellationToken);

        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var older = await _operations.GetOlderThanAsync(cutoff, TestContext.Current.CancellationToken);

        Assert.Single(older);
        Assert.Equal(old.Id, older[0].Id);

        await _operations.DeleteOlderThanAsync(cutoff, TestContext.Current.CancellationToken);
        Assert.Empty(await _operations.GetAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RootPresetRepository_ShouldRoundTrip()
    {
        var preset = new RootPreset
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "仓库 A",
            Path = @"D:\repo-a",
            SortOrder = 1,
        };

        await _presets.AddAsync(preset, TestContext.Current.CancellationToken);

        var all = await _presets.GetAllAsync(TestContext.Current.CancellationToken);
        Assert.Single(all);
        Assert.Equal(@"D:\repo-a", all[0].Path);

        preset.Name = "改过名";
        await _presets.UpdateAsync(preset, TestContext.Current.CancellationToken);
        Assert.Equal("改过名", (await _presets.GetAllAsync(TestContext.Current.CancellationToken))[0].Name);

        await _presets.DeleteAsync(preset.Id, TestContext.Current.CancellationToken);
        Assert.Empty(await _presets.GetAllAsync(TestContext.Current.CancellationToken));
    }
}
