using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Core.Utils;
using FileStash.Infrastructure.Data;
using FileStash.Infrastructure.Services;

namespace FileStash.Core.Tests;

public sealed class StashServiceTests : IAsyncLifetime
{
    private readonly TempWorkspace _workspace = new();

    private StashService _service = null!;
    private IStashItemRepository _items = null!;
    private IStashEntryRepository _entries = null!;
    private IStashGroupRepository _groups = null!;
    private IBlobStore _blobStore = null!;
    private string _groupId = null!;

    public async ValueTask InitializeAsync()
    {
        var migrator = new DatabaseMigrator(_workspace.ConnectionFactory);
        await migrator.MigrateAsync(TestContext.Current.CancellationToken);

        _groups = new StashGroupRepository(_workspace.ConnectionFactory);
        _items = new StashItemRepository(_workspace.ConnectionFactory);
        _entries = new StashEntryRepository(_workspace.ConnectionFactory);
        _blobStore = new BlobStore(_workspace.Paths, TimeSpan.Zero);

        var settings = new SettingsService(_workspace.Paths);
        settings.Load();

        _service = new StashService(
            _groups,
            _items,
            _entries,
            _blobStore,
            new GitService(),
            settings);

        var group = await _service.CreateGroupAsync("测试组");
        _groupId = group.Id;
    }

    public ValueTask DisposeAsync()
    {
        _workspace.Dispose();
        return ValueTask.CompletedTask;
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceStatusMustDetectAddedFileOrEmptyDirectory(bool emptyDirectory)
    {
        var folder = Path.Combine(_workspace.Root, "source");
        Directory.CreateDirectory(folder);
        _workspace.CreateFile("source/a.txt", "a");
        await _service.AddAsync(_groupId, [folder], null, TestContext.Current.CancellationToken);
        if (emptyDirectory) Directory.CreateDirectory(Path.Combine(folder, "new"));
        else _workspace.CreateFile("source/new.txt", "new");
        var statuses = await _service.CheckSourceStatusAsync(_groupId, TestContext.Current.CancellationToken);
        Assert.Equal(ItemSourceState.Modified, statuses.Single().State);
    }


    [Fact]
    public async Task SourceStatusMustIgnoreExcludedAdditions()
    {
        var folder = Path.Combine(_workspace.Root, "source");
        Directory.CreateDirectory(folder);
        _workspace.CreateFile("source/a.txt", "a");
        var settings = new SettingsService(_workspace.Paths);
        settings.Current.ExcludeGlobs = ["**/*.tmp"];
        var service = new StashService(_groups, _items, _entries, _blobStore, new GitService(), settings);
        await service.AddAsync(_groupId, [folder], null, TestContext.Current.CancellationToken);
        _workspace.CreateFile("source/ignored.tmp", "ignored");
        Assert.Equal(ItemSourceState.Unchanged,
            (await service.CheckSourceStatusAsync(_groupId, TestContext.Current.CancellationToken)).Single().State);
    }

    [Fact]
    public async Task AddAsync_WithFile_ShouldCreateItemWithOneEntry()
    {
        var source = _workspace.CreateFile(Path.Combine("data", "a.txt"), "hello");

        var result = await _service.AddAsync(_groupId, [source], null, TestContext.Current.CancellationToken);

        Assert.Single(result.Added);

        var item = result.Added[0];
        Assert.Equal(StashItemKind.File, item.Kind);
        Assert.Equal("a.txt", item.DisplayName);
        Assert.Equal(1, item.FileCount);
        Assert.Equal(5, item.TotalSize);

        var entries = await _entries.GetByItemAsync(item.Id, TestContext.Current.CancellationToken);
        Assert.Single(entries);
        Assert.NotNull(entries[0].Sha256);
        Assert.True(_blobStore.Exists(entries[0].Sha256!));
    }

    [Fact]
    public async Task AddAsync_WithFolder_ShouldRecordNestedFilesAndEmptyDirectories()
    {
        _workspace.CreateFile(Path.Combine("tree", "a.txt"), "a");
        _workspace.CreateFile(Path.Combine("tree", "sub", "b.txt"), "bb");
        Directory.CreateDirectory(Path.Combine(_workspace.Root, "tree", "empty"));

        var folder = Path.Combine(_workspace.Root, "tree");

        var result = await _service.AddAsync(_groupId, [folder], null, TestContext.Current.CancellationToken);

        Assert.Single(result.Added);

        var item = result.Added[0];
        Assert.Equal(StashItemKind.Folder, item.Kind);
        Assert.Equal(2, item.FileCount);
        Assert.Equal(3, item.TotalSize);

        var entries = await _entries.GetByItemAsync(item.Id, TestContext.Current.CancellationToken);

        // 两个文件 + 一个空目录
        Assert.Equal(3, entries.Count);
        Assert.Single(entries, entry => entry.IsDirectory);
        Assert.Equal("tree\\empty", entries.Single(entry => entry.IsDirectory).RelativePath);
    }

    [Fact]
    public async Task AddAsync_SameSourceTwice_ShouldReportDuplicate()
    {
        var source = _workspace.CreateFile("a.txt", "hello");

        await _service.AddAsync(_groupId, [source], null, TestContext.Current.CancellationToken);
        var second = await _service.AddAsync(_groupId, [source], null, TestContext.Current.CancellationToken);

        Assert.Empty(second.Added);
        Assert.Single(second.Duplicates);
    }

    [Fact]
    public async Task AddAsync_WithSamePathDifferentCase_ShouldReportDuplicate()
    {
        var source = _workspace.CreateFile("a.txt", "hello");

        await _service.AddAsync(_groupId, [source], null, TestContext.Current.CancellationToken);

        var variant = source.ToUpperInvariant();
        var second = await _service.AddAsync(_groupId, [variant], null, TestContext.Current.CancellationToken);

        // 路径不存在 → 算失败；关键是它没有被当成"新的一个"加进去
        Assert.Empty(second.Added);
    }

    [Fact]
    public async Task AddAsync_MissingPath_ShouldReportFailure()
    {
        var result = await _service.AddAsync(
            _groupId,
            [Path.Combine(_workspace.Root, "nope.txt")],
            null,
            TestContext.Current.CancellationToken);

        Assert.Empty(result.Added);
        Assert.Single(result.Failed);
    }

    [Fact]
    public async Task AddAsync_ShouldSnapshotContentSoLaterEditsDoNotAffectIt()
    {
        var source = _workspace.CreateFile("a.txt", "原始内容");

        var result = await _service.AddAsync(_groupId, [source], null, TestContext.Current.CancellationToken);
        var item = result.Added[0];

        var entry = (await _entries.GetByItemAsync(item.Id, TestContext.Current.CancellationToken)).Single();
        var blobPath = _blobStore.GetBlobPath(entry.Sha256!);

        // 改源文件
        await File.WriteAllTextAsync(source, "改过的内容", TestContext.Current.CancellationToken);

        // 暂存内容不变
        Assert.Equal("原始内容", await File.ReadAllTextAsync(blobPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefreshSnapshotAsync_ShouldPickUpNewContent()
    {
        var source = _workspace.CreateFile("a.txt", "原始内容");

        var result = await _service.AddAsync(_groupId, [source], null, TestContext.Current.CancellationToken);
        var item = result.Added[0];

        await File.WriteAllTextAsync(source, "改过的内容", TestContext.Current.CancellationToken);

        await _service.RefreshSnapshotAsync([item.Id], null, TestContext.Current.CancellationToken);

        var entry = (await _entries.GetByItemAsync(item.Id, TestContext.Current.CancellationToken)).Single();
        var blobPath = _blobStore.GetBlobPath(entry.Sha256!);

        Assert.Equal("改过的内容", await File.ReadAllTextAsync(blobPath, TestContext.Current.CancellationToken));
        Assert.Equal(1, item.FileCount);
    }

    [Fact]
    public async Task SetRootAsync_ShouldRecomputeRelativePaths()
    {
        _workspace.CreateFile(Path.Combine("container", "tree", "sub", "b.txt"), "bb");

        var folder = Path.Combine(_workspace.Root, "container", "tree");

        var result = await _service.AddAsync(_groupId, [folder], null, TestContext.Current.CancellationToken);
        var item = result.Added[0];

        // 默认根 = 父目录
        Assert.Equal(Path.Combine(_workspace.Root, "container"), item.RootPath);
        Assert.Equal("tree", item.RelativePath);

        await _service.SetRootAsync([item.Id], _workspace.Root);

        var reloaded = await _items.GetByIdsAsync([item.Id], TestContext.Current.CancellationToken);
        var updated = reloaded.Single();

        Assert.Equal(PathUtil.Normalize(_workspace.Root), updated.RootPath);
        Assert.Equal("container\\tree", updated.RelativePath);

        var entries = await _entries.GetByItemAsync(item.Id, TestContext.Current.CancellationToken);
        Assert.Equal("container\\tree\\sub\\b.txt", entries.Single().RelativePath);
    }

    [Fact]
    public async Task SetRootAsync_ShouldRejectNonAncestorRoot()
    {
        _workspace.CreateFile(Path.Combine("tree", "a.txt"), "a");
        _workspace.CreateFile(Path.Combine("other", "b.txt"), "b");

        var source = Path.Combine(_workspace.Root, "tree", "a.txt");

        var result = await _service.AddAsync(_groupId, [source], null, TestContext.Current.CancellationToken);
        var item = result.Added[0];

        // "other" 不是 "tree\a.txt" 的祖先
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.SetRootAsync([item.Id], Path.Combine(_workspace.Root, "other")));
    }

    [Fact]
    public async Task SetRootAsync_ShouldRejectTheSourcePathItselfAsRoot()
    {
        _workspace.CreateFile(Path.Combine("tree", "a.txt"), "a");

        var source = Path.Combine(_workspace.Root, "tree", "a.txt");

        var result = await _service.AddAsync(_groupId, [source], null, TestContext.Current.CancellationToken);
        var item = result.Added[0];

        // 根等于源路径的话，相对路径会变成空串，写回去时这一层就没了
        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.SetRootAsync([item.Id], source));
    }

    [Fact]
    public async Task MoveItemsAsync_ShouldChangeGroup()
    {
        var source = _workspace.CreateFile("a.txt", "hello");

        var result = await _service.AddAsync(_groupId, [source], null, TestContext.Current.CancellationToken);
        var item = result.Added[0];

        var target = await _service.CreateGroupAsync("另一个组");
        await _service.MoveItemsAsync([item.Id], target.Id);

        Assert.Empty(await _items.GetByGroupAsync(_groupId, TestContext.Current.CancellationToken));
        Assert.Single(await _items.GetByGroupAsync(target.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveItemsAsync_ShouldDeleteItems()
    {
        var source = _workspace.CreateFile("a.txt", "hello");

        var result = await _service.AddAsync(_groupId, [source], null, TestContext.Current.CancellationToken);
        await _service.RemoveItemsAsync([result.Added[0].Id]);

        Assert.Empty(await _items.GetByGroupAsync(_groupId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckSourceStatusAsync_ShouldDetectModifiedAndMissing()
    {
        var source = _workspace.CreateFile("a.txt", "hello");

        var result = await _service.AddAsync(_groupId, [source], null, TestContext.Current.CancellationToken);
        var item = result.Added[0];

        var unchanged = await _service.CheckSourceStatusAsync(_groupId, TestContext.Current.CancellationToken);
        Assert.Equal(ItemSourceState.Unchanged, unchanged.Single().State);

        // 改内容但保持同样大小 → 靠哈希认出来
        await File.WriteAllTextAsync(source, "world", TestContext.Current.CancellationToken);

        var modified = await _service.CheckSourceStatusAsync(_groupId, TestContext.Current.CancellationToken);
        Assert.Equal(ItemSourceState.Modified, modified.Single().State);

        File.Delete(source);

        var missing = await _service.CheckSourceStatusAsync(_groupId, TestContext.Current.CancellationToken);
        Assert.Equal(ItemSourceState.Missing, missing.Single().State);
    }

    [Fact]
    public async Task AddAsync_InsideGitRepository_ShouldUseRepoRootAsRoot()
    {
        if (!GitHelper.IsAvailable)
        {
            return;
        }

        var repo = Path.Combine(_workspace.Root, "repo");
        Directory.CreateDirectory(repo);
        GitHelper.InitRepository(repo);

        var source = _workspace.CreateFile(Path.Combine("repo", "src", "a.txt"), "hello");
        GitHelper.CommitAll(repo, "init");

        var result = await _service.AddAsync(_groupId, [source], null, TestContext.Current.CancellationToken);
        var item = result.Added[0];

        Assert.NotNull(item.GitRepoRoot);
        Assert.Equal(PathUtil.Normalize(repo), item.GitRepoRoot);
        Assert.Equal(PathUtil.Normalize(repo), item.RootPath);
        Assert.Equal("src\\a.txt", item.RelativePath);
        Assert.NotNull(item.GitBranch);
        Assert.False(string.IsNullOrEmpty(item.GitCommit));
    }
}
