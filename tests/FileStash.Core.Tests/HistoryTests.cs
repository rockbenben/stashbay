using FileStash.App.ViewModels;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Services;

namespace FileStash.Core.Tests;

public sealed class HistoryTests : IAsyncLifetime
{
    private readonly ApplyTestContext _context = new();

    public async ValueTask InitializeAsync() => await _context.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>假的对话框服务：测试里不该弹窗。</summary>
    private sealed class SilentDialogService : App.Services.IDialogService
    {
        public bool Confirmed { get; set; } = true;

        public IReadOnlyList<string>? PickStashPaths() => null;

        public string? PickFolder() => null;

        public string? PromptText(string title, string initialValue) => null;

        public bool Confirm(string title, string message) => Confirmed;

        public string? PickSetRoot(string sourcePath) => null;

        public string? PickGroup(IReadOnlyList<(string Id, string Name)> groups, string currentGroupId) => null;

        public IReadOnlyList<string>? GetClipboardPaths() => null;


        public void SetClipboardPaths(IReadOnlyList<string> paths)
        {
        }

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowWarning(string title, string message)
        {
        }

        public void ShowToast(string title, string summary)
        {
        }
    }

    [Fact]
    public async Task HistoryViewModel_ShouldListOperationsAndUndoSelectedOne()
    {
        var groupId = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile("a.txt", "暂存内容");

        var addResult = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);

        var target = Path.Combine(_context.Workspace.Root, "out");
        var plan = await _context.Planner.BuildPlanAsync(
            addResult.Added,
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var operation = await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(target, "a.txt")));

        var viewModel = new HistoryViewModel(
            _context.Operations,
            _context.OperationEntries,
            _context.Groups,
            _context.Executor,
            new SilentDialogService());

        await viewModel.LoadAsync();

        var item = Assert.Single(viewModel.Operations);
        Assert.Equal(operation.Id, item.Id);
        Assert.True(item.CanUndo);

        viewModel.SelectedOperation = item;
        await viewModel.UndoCommand.ExecuteAsync(null);

        Assert.False(File.Exists(Path.Combine(target, "a.txt")));
    }

    [Fact]
    public async Task HistoryViewModel_ShouldSkipUndoWhenFileChangedAfterwards()
    {
        var groupId = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile("a.txt", "暂存内容");

        var addResult = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);

        var target = Path.Combine(_context.Workspace.Root, "out");
        var plan = await _context.Planner.BuildPlanAsync(
            addResult.Added,
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        // 写完之后文件又被改了 → 撤销要跳过，不能乱删
        await File.WriteAllTextAsync(Path.Combine(target, "a.txt"), "别人改的", TestContext.Current.CancellationToken);

        var viewModel = new HistoryViewModel(
            _context.Operations,
            _context.OperationEntries,
            _context.Groups,
            _context.Executor,
            new SilentDialogService());

        await viewModel.LoadAsync();
        viewModel.SelectedOperation = viewModel.Operations[0];

        await viewModel.UndoCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
        Assert.Equal("别人改的", await File.ReadAllTextAsync(Path.Combine(target, "a.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cleanup_ShouldRemoveDeletedItemsBlobsButKeepBackups()
    {
        // 暂存一个文件 → 写到一个已存在的目标上（产生一份备份）
        var groupId = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile("a.txt", "新内容");

        var addResult = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);
        var entry = (await _context.Entries.GetByItemAsync(addResult.Added[0].Id, TestContext.Current.CancellationToken)).Single();

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "a.txt"), "旧内容", TestContext.Current.CancellationToken);

        var plan = await _context.Planner.BuildPlanAsync(
            addResult.Added,
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var operation = await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);
        var backupSha = (await _context.OperationEntries.GetByOperationAsync(operation.Id, TestContext.Current.CancellationToken))
            .Single(item => item.Action == EntryResult.Overwritten).BackupSha256;

        Assert.NotNull(backupSha);

        // 删掉暂存项 → 它的 blob 没人引用了
        await _context.Stash.RemoveItemsAsync([addResult.Added[0].Id]);

        var cleanup = new CleanupService(
            _context.Entries,
            _context.OperationEntries,
            _context.Operations,
            _context.BlobStore,
            _context.Paths);

        var deleted = await cleanup.DeleteUnreferencedBlobsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);

        // 暂存内容的 blob 没了
        Assert.False(_context.BlobStore.Exists(entry.Sha256!));

        // 备份还在 —— 否则那次操作就撤不回来了
        Assert.True(_context.BlobStore.Exists(backupSha!));

        var undo = await _context.Executor.UndoAsync(operation.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, undo.Restored);
        Assert.Equal("旧内容", await File.ReadAllTextAsync(Path.Combine(target, "a.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SettingsViewModel_ShouldReportStorageAndCleanUnreferenced()
    {
        var groupId = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile("a.txt", "内容");

        var addResult = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);
        var entry = (await _context.Entries.GetByItemAsync(addResult.Added[0].Id, TestContext.Current.CancellationToken)).Single();

        var settings = new SettingsService(_context.Paths);
        settings.Load();

        var viewModel = new SettingsViewModel(
            settings,
            new CleanupService(_context.Entries, _context.OperationEntries, _context.Operations, _context.BlobStore, _context.Paths),
            _context.BlobStore,
            _context.Operations,
            new SilentDialogService());

        await viewModel.RefreshStorageAsync();

        Assert.Contains("暂存内容", viewModel.StorageText);

        // 删掉暂存项再清理
        await _context.Stash.RemoveItemsAsync([addResult.Added[0].Id]);
        await viewModel.CleanupUnreferencedCommand.ExecuteAsync(null);

        Assert.False(_context.BlobStore.Exists(entry.Sha256!));
    }
}
