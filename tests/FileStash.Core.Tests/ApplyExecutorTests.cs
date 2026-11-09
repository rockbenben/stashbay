using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Infrastructure.Native;
using FileStash.Core.Resources;

namespace FileStash.Core.Tests;

public sealed class ApplyExecutorTests : IAsyncLifetime
{
    private readonly ApplyTestContext _context = new();

    public async ValueTask InitializeAsync() => await _context.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<StashItem> AddFileAsync(string relativePath, string content)
    {
        var groupId = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile(relativePath, content);

        var result = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);

        return result.Added[0];
    }

    [Fact]
    public async Task ExecuteAsync_ShouldWriteContentToTarget()
    {
        var item = await AddFileAsync("a.txt", "暂存的内容");

        var target = Path.Combine(_context.Workspace.Root, "out");
        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var operation = await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.Equal("暂存的内容", await File.ReadAllTextAsync(Path.Combine(target, "a.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBackupOverwrittenFileAndUndoShouldRestoreIt()
    {
        var item = await AddFileAsync("a.txt", "新内容");

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);
        var targetFile = Path.Combine(target, "a.txt");
        await File.WriteAllTextAsync(targetFile, "旧内容", TestContext.Current.CancellationToken);

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var operation = await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.Equal("新内容", await File.ReadAllTextAsync(targetFile, TestContext.Current.CancellationToken));

        var entries = await _context.OperationEntries.GetByOperationAsync(operation.Id, TestContext.Current.CancellationToken);
        Assert.NotNull(entries.Single(entry => entry.Action == EntryResult.Overwritten).BackupSha256);

        // 撤销 → 恢复成旧内容
        var undo = await _context.Executor.UndoAsync(operation.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, undo.Restored);
        Assert.Equal("旧内容", await File.ReadAllTextAsync(targetFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UndoAsync_ShouldDeleteCreatedFile()
    {
        var item = await AddFileAsync("a.txt", "内容");

        var target = Path.Combine(_context.Workspace.Root, "out");
        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var operation = await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(target, "a.txt")));

        var undo = await _context.Executor.UndoAsync(operation.Id, TestContext.Current.CancellationToken);

        // 文件被删掉；顺手建出来的空目录也会被删掉
        Assert.False(File.Exists(Path.Combine(target, "a.txt")));
        Assert.False(Directory.Exists(target));
        Assert.True(undo.Deleted >= 1);
    }

    [Fact]
    public async Task UndoAsync_ShouldSkipWhenFileChangedAfterwards()
    {
        var item = await AddFileAsync("a.txt", "内容");

        var target = Path.Combine(_context.Workspace.Root, "out");
        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var operation = await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        // 写完之后又被人改了
        await File.WriteAllTextAsync(Path.Combine(target, "a.txt"), "别人改的", TestContext.Current.CancellationToken);

        var undo = await _context.Executor.UndoAsync(operation.Id, TestContext.Current.CancellationToken);

        // 文件没被删（内容变过了），目录里还有它所以目录也不删
        Assert.Equal(0, undo.Deleted);
        Assert.True(undo.Skipped >= 1);
        Assert.Equal("别人改的", await File.ReadAllTextAsync(Path.Combine(target, "a.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldKeepReadOnlyAttributeAfterOverwrite()
    {
        var item = await AddFileAsync("a.txt", "新内容");

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);
        var targetFile = Path.Combine(target, "a.txt");
        await File.WriteAllTextAsync(targetFile, "旧内容", TestContext.Current.CancellationToken);
        File.SetAttributes(targetFile, File.GetAttributes(targetFile) | FileAttributes.ReadOnly);

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.Equal("新内容", await File.ReadAllTextAsync(targetFile, TestContext.Current.CancellationToken));
        Assert.True(File.GetAttributes(targetFile).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReportLockingProcessAndContinueWithOthers()
    {
        var item = await AddFileAsync("a.txt", "内容");

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);
        var lockedFile = Path.Combine(target, "a.txt");
        await File.WriteAllTextAsync(lockedFile, "旧内容", TestContext.Current.CancellationToken);

        // 独占打开，别人动不了
        await using var hold = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None);

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var operation = await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.PartiallyFailed, operation.Status);

        var entries = await _context.OperationEntries.GetByOperationAsync(operation.Id, TestContext.Current.CancellationToken);
        var failed = entries.Single(entry => entry.Action == EntryResult.Failed);

        Assert.Contains("占用", failed.Error);
        Assert.Contains(".exe", failed.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSkipIdenticalFiles()
    {
        var item = await AddFileAsync("a.txt", "一样的");

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "a.txt"), "一样的", TestContext.Current.CancellationToken);

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var operation = await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        var entries = await _context.OperationEntries.GetByOperationAsync(operation.Id, TestContext.Current.CancellationToken);
        Assert.Equal(EntryResult.Skipped, entries.Single().Action);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRecordCreatedDirectoriesForUndo()
    {
        var groupId = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile(Path.Combine("tree", "sub", "a.txt"), "内容");

        var result = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);
        var item = result.Added[0];

        var target = Path.Combine(_context.Workspace.Root, "out");

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var operation = await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        var entries = await _context.OperationEntries.GetByOperationAsync(operation.Id, TestContext.Current.CancellationToken);
        Assert.Contains(entries, entry => entry.Action == EntryResult.DirectoryCreated);

        // 撤销：文件删掉，空目录也删掉
        var undo = await _context.Executor.UndoAsync(operation.Id, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(Path.Combine(target, "a.txt")));
        Assert.False(Directory.Exists(target));
        Assert.True(undo.Deleted >= 1);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSupportCancellation()
    {
        var groupId = await _context.CreateGroupAsync();
        var first = _context.Workspace.CreateFile("one/a.txt", "a");
        var second = _context.Workspace.CreateFile("two/b.txt", "b");

        var result = await _context.Stash.AddAsync(groupId, [first, second], null, TestContext.Current.CancellationToken);

        var target = Path.Combine(_context.Workspace.Root, "out");

        var plan = await _context.Planner.BuildPlanAsync(
            result.Added,
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();

        // 第一个条目写完就取消：已完成的保留，后面的不做
        var progress = new CancellingProgress(cancellation);

        var operation = await _context.Executor.ExecuteAsync(plan, progress, cancellation.Token);

        Assert.Equal(OperationStatus.Cancelled, operation.Status);

        // 文件项的相对路径就是文件名，所以目标是 out\a.txt 与 out\b.txt
        Assert.True(File.Exists(Path.Combine(target, "a.txt")));
        Assert.False(File.Exists(Path.Combine(target, "b.txt")));

        // 取消的操作也能撤销
        var undo = await _context.Executor.UndoAsync(operation.Id, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(target, "a.txt")));
    }

    /// <summary>写到第几个条目就取消。</summary>
    [Fact]
    public async Task ExecuteAsync_ShouldDescribeLockedTargetInUiLanguage_NotAsRawException()
    {
        // 目标被另一个句柄占着 → 写回失败。历史上这里直接把 ex.Message 端上去，
        // 中文界面里出现的是一整句英文（还被历史列宽切掉后半句）。
        var item = await AddFileAsync("a.txt", "新内容");

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);
        var targetFile = Path.Combine(target, "a.txt");
        await File.WriteAllTextAsync(targetFile, "旧内容", TestContext.Current.CancellationToken);

        using var hold = new FileStream(targetFile, FileMode.Open, FileAccess.Write, FileShare.None);

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var operation = await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        var entries = await _context.OperationEntries.GetByOperationAsync(
            operation.Id, TestContext.Current.CancellationToken);
        var failed = Assert.Single(entries, entry => entry.Action == EntryResult.Failed);

        Assert.NotNull(failed.Error);
        // 英文整句的形状：以 ASCII 字母开头、里头有空格分词（"The process cannot access …"）
        Assert.DoesNotMatch(@"^[A-Za-z].*\s[A-Za-z]+\s", failed.Error);
        // 正向也要坐实：说的是"占用"这件事，而不是随便一句本地化文案
        Assert.Contains(
            Strings.LockedByProcess[..Strings.LockedByProcess.IndexOf('{')],
            failed.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStillSayOccupied_WhenNobodyCanNameTheHolder()
    {
        // 查不到占用者是常态（网络共享、对方已退出、RestartManager 失灵）。
        // 那一档不能退化成整句英文 —— 用户要的是"这个文件正被谁占着"。
        var item = await AddFileAsync("a.txt", "新内容");

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);
        var targetFile = Path.Combine(target, "a.txt");
        await File.WriteAllTextAsync(targetFile, "旧内容", TestContext.Current.CancellationToken);

        using var hold = new FileStream(targetFile, FileMode.Open, FileAccess.Write, FileShare.None);

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var blind = new ApplyExecutor(
            _context.BlobStore,
            _context.Operations,
            _context.OperationEntries,
            new AtomicFileWriter(),
            new BlindLockInspector(),
            _context.Settings);

        var operation = await blind.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        var entries = await _context.OperationEntries.GetByOperationAsync(
            operation.Id, TestContext.Current.CancellationToken);
        var failed = Assert.Single(entries, entry => entry.Action == EntryResult.Failed);

        Assert.Equal(
            Strings.Format(Strings.LockedByProcess, Strings.UnknownProcess),
            failed.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFormatUnmappedIoException_WithWriteFailedDetailPrefix()
    {
        // 非共享冲突且非权限问题的未知 I/O 错误（如磁盘已满、设备未就绪），
        // 需用 WriteFailedDetail 包装（"写入失败：{0}"），而不是把原始错误直出。
        var item = await AddFileAsync("a.txt", "新内容");
        var target = Path.Combine(_context.Workspace.Root, "out");

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var failingWriter = new FailingFileWriter(new IOException("模拟磁盘错误"));
        var executor = new ApplyExecutor(
            _context.BlobStore,
            _context.Operations,
            _context.OperationEntries,
            failingWriter,
            new BlindLockInspector(),
            _context.Settings);

        var operation = await executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        var entries = await _context.OperationEntries.GetByOperationAsync(
            operation.Id, TestContext.Current.CancellationToken);
        var failed = Assert.Single(entries, entry => entry.Action == EntryResult.Failed);

        Assert.Equal(
            Strings.Format(Strings.WriteFailedDetail, "模拟磁盘错误"),
            failed.Error);
    }

    private sealed class FailingFileWriter(Exception exception) : IAtomicFileWriter
    {
        public Task<bool> ReplaceAsync(string temporaryPath, string targetPath, CancellationToken ct) =>
            Task.FromException<bool>(exception);
    }

    /// <summary>什么都查不到的占用者探测器：只用来走"报不出名字"那一档。</summary>
    private sealed class BlindLockInspector : IFileLockInspector
    {
        public Task<IReadOnlyList<string>> GetLockingProcessesAsync(string path, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class CancellingProgress : IProgress<ProgressInfo>
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingProgress(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public void Report(ProgressInfo value)
        {
            if (value.Done >= 1)
            {
                _cancellation.Cancel();
            }
        }
    }
}
