using FileStash.Core.Models;
using FileStash.Core.Services;

namespace FileStash.Core.Tests;

public sealed class DragExportTests : IAsyncLifetime
{
    private readonly ApplyTestContext _context = new();

    public async ValueTask InitializeAsync() => await _context.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task MaterializeAsync_WithFolderAndFile_ShouldReturnTopLevelPaths()
    {
        var groupId = await _context.CreateGroupAsync();

        // 一个文件夹（里面有子目录）
        _context.Workspace.CreateFile(Path.Combine("tree", "sub", "a.txt"), "文件夹里的内容");

        // 一个单独的文件
        var single = _context.Workspace.CreateFile("b.txt", "单个文件");

        var result = await _context.Stash.AddAsync(
            groupId,
            [Path.Combine(_context.Workspace.Root, "tree"), single],
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Added.Count);

        var export = new DragExportService(_context.Entries, _context.BlobStore, _context.Paths);

        var paths = await export.MaterializeAsync(result.Added, TestContext.Current.CancellationToken);

        // 顶层：一个文件夹 + 一个文件
        Assert.Equal(2, paths.Count);
        Assert.Single(paths, path => Path.GetFileName(path) == "tree");
        Assert.Single(paths, path => Path.GetFileName(path) == "b.txt");
    }

    [Fact]
    public async Task MaterializeAsync_ShouldPreserveFolderStructure()
    {
        var groupId = await _context.CreateGroupAsync();
        _context.Workspace.CreateFile(Path.Combine("tree", "sub", "a.txt"), "内容");

        var result = await _context.Stash.AddAsync(
            groupId,
            [Path.Combine(_context.Workspace.Root, "tree")],
            null,
            TestContext.Current.CancellationToken);

        var export = new DragExportService(_context.Entries, _context.BlobStore, _context.Paths);
        var paths = await export.MaterializeAsync(result.Added, TestContext.Current.CancellationToken);

        var top = Assert.Single(paths);

        Assert.True(Directory.Exists(top));
        Assert.Equal("内容", await File.ReadAllTextAsync(Path.Combine(top, "sub", "a.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MaterializeAsync_ShouldCreateEmptyDirectories()
    {
        var groupId = await _context.CreateGroupAsync();
        Directory.CreateDirectory(Path.Combine(_context.Workspace.Root, "tree", "empty"));

        var source = Path.Combine(_context.Workspace.Root, "tree");
        var result = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);

        var export = new DragExportService(_context.Entries, _context.BlobStore, _context.Paths);
        var paths = await export.MaterializeAsync(result.Added, TestContext.Current.CancellationToken);

        var top = Assert.Single(paths);
        Assert.True(Directory.Exists(Path.Combine(top, "empty")));
    }

    [Fact]
    public async Task MaterializeAsync_ForFileItem_ShouldProduceThatFile()
    {
        var groupId = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile("a.txt", "内容");

        var result = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);

        var export = new DragExportService(_context.Entries, _context.BlobStore, _context.Paths);
        var paths = await export.MaterializeAsync(result.Added, TestContext.Current.CancellationToken);

        var top = Assert.Single(paths);

        Assert.True(File.Exists(top));
        Assert.Equal("内容", await File.ReadAllTextAsync(top, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MaterializeAsync_WithNoItems_ShouldReturnEmpty()
    {
        var export = new DragExportService(_context.Entries, _context.BlobStore, _context.Paths);

        var paths = await export.MaterializeAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(paths);
    }
}

public sealed class CleanupServiceTests : IAsyncLifetime
{
    private readonly ApplyTestContext _context = new();

    public async ValueTask InitializeAsync() => await _context.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    private CleanupService CreateService() => new(
        _context.Entries,
        _context.OperationEntries,
        _context.Operations,
        _context.BlobStore,
        _context.Paths);

    [Fact]
    public async Task DeleteExpiredExportsAsync_ShouldRemoveOldDirectoriesOnly()
    {
        var old = Path.Combine(_context.Paths.ExportDirectory, "old");
        var fresh = Path.Combine(_context.Paths.ExportDirectory, "fresh");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(fresh);

        // 把 old 的时间戳改成 48 小时前
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-48));

        var deleted = await CreateService().DeleteExpiredExportsAsync(TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public async Task DeleteExpiredExportsAsync_ShouldRemoveReadOnlyFiles()
    {
        var old = Path.Combine(_context.Paths.ExportDirectory, "old");
        Directory.CreateDirectory(old);

        var file = Path.Combine(old, "a.txt");
        await File.WriteAllTextAsync(file, "内容", TestContext.Current.CancellationToken);
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-48));

        var deleted = await CreateService().DeleteExpiredExportsAsync(TimeSpan.FromHours(24), TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public async Task DeleteExpiredExportsAsync_ZeroMaxAge_ShouldRemoveAllDirectories()
    {
        var dir1 = Path.Combine(_context.Paths.ExportDirectory, "export1");
        var dir2 = Path.Combine(_context.Paths.ExportDirectory, "export2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        var deleted = await CreateService().DeleteExpiredExportsAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.Equal(2, deleted);
        Assert.False(Directory.Exists(dir1));
        Assert.False(Directory.Exists(dir2));
    }

    [Fact]
    public async Task DeleteUnreferencedBlobsAsync_ShouldKeepReferencedOnes()
    {
        var groupId = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile("a.txt", "内容");

        var result = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);
        var entry = (await _context.Entries.GetByItemAsync(result.Added[0].Id, TestContext.Current.CancellationToken)).Single();

        // 再放一个没人引用的 blob
        var orphan = _context.Workspace.CreateFile("orphan.txt", "没人要");
        var (orphanSha, _) = await _context.BlobStore.PutFileAsync(orphan, TestContext.Current.CancellationToken);

        var deleted = await CreateService().DeleteUnreferencedBlobsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.True(_context.BlobStore.Exists(entry.Sha256!));
        Assert.False(_context.BlobStore.Exists(orphanSha));
    }

    [Fact]
    public async Task DeleteExpiredHistoryAsync_ShouldDeleteOldOperations()
    {
        var old = new Operation
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.UtcNow.AddDays(-40),
            TargetMode = TargetMode.OriginalLocation,
            TargetDescription = "原位置",
            Status = OperationStatus.Succeeded,
        };

        await _context.Operations.AddAsync(old, TestContext.Current.CancellationToken);

        var deleted = await CreateService().DeleteExpiredHistoryAsync(30, TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.Empty(await _context.Operations.GetAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteExpiredHistoryAsync_WithZeroRetention_ShouldKeepEverything()
    {
        var old = new Operation
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.UtcNow.AddDays(-400),
            TargetMode = TargetMode.OriginalLocation,
            TargetDescription = "原位置",
            Status = OperationStatus.Succeeded,
        };

        await _context.Operations.AddAsync(old, TestContext.Current.CancellationToken);

        var deleted = await CreateService().DeleteExpiredHistoryAsync(0, TestContext.Current.CancellationToken);

        Assert.Equal(0, deleted);
        Assert.Single(await _context.Operations.GetAllAsync(TestContext.Current.CancellationToken));
    }
}
