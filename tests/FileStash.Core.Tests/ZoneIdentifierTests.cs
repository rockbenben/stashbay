
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Infrastructure.Native;

namespace FileStash.Core.Tests;

public sealed class ZoneIdentifierTests : IAsyncLifetime
{
    private readonly ApplyTestContext _context = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private static readonly byte[] Download = System.Text.Encoding.UTF8.GetBytes(
        "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.test/download\r\n");
    private static readonly byte[] Restricted = System.Text.Encoding.Unicode.GetBytes("[ZoneTransfer]\r\nZoneId=4\r\n");

    public async ValueTask InitializeAsync() => await _context.Migrator.MigrateAsync(_ct);
    public ValueTask DisposeAsync() { _context.Dispose(); return ValueTask.CompletedTask; }

    private static void SetZone(string path, byte[]? zone)
    {
        if (zone is null) File.Delete(path + ":Zone.Identifier");
        else File.WriteAllBytes(path + ":Zone.Identifier", zone);
    }

    private static byte[]? ReadZone(string path) =>
        File.Exists(path + ":Zone.Identifier") ? File.ReadAllBytes(path + ":Zone.Identifier") : null;

    private async Task<StashItem> Add(string name, byte[]? zone)
    {
        var path = _context.Workspace.CreateFile("source/" + name, "snapshot");
        SetZone(path, zone);
        var group = await _context.CreateGroupAsync();
        return (await _context.Stash.AddAsync(group, [path], null, _ct)).Added.Single();
    }

    private Task<ApplyPlan> Plan(StashItem item) => _context.Planner.BuildPlanAsync([item],
        new TargetSpec(TargetMode.SpecifiedPath, Path.Combine(_context.Workspace.Root, "out"), StructureMode.Flatten),
        ConflictPolicy.Overwrite, _ct);

    [Fact]
    public async Task SameContentMustKeepIndependentOriginBytes()
    {
        var first = await Add("a.txt", Download);
        var second = await Add("b.txt", Restricted);
        var third = await Add("c.txt", null);
        var export = new DragExportService(_context.Entries, _context.BlobStore, _context.Paths);
        var paths = await export.MaterializeAsync([first, second, third], _ct);
        Assert.Equal(Download, ReadZone(paths[0]));
        Assert.Equal(Restricted, ReadZone(paths[1]));
        Assert.Null(ReadZone(paths[2]));
        var hashes = new List<string?>();
        foreach (var item in new[] { first, second, third })
            hashes.Add((await _context.Entries.GetByItemAsync(item.Id, _ct)).Single().Sha256);
        Assert.Single(hashes.Distinct());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshMustCaptureAddedOrRemovedOrigin(bool remove)
    {
        var item = await Add("a.txt", remove ? Download : null);
        SetZone(item.SourcePath, remove ? null : Restricted);
        await _context.Stash.RefreshSnapshotAsync([item.Id], null, _ct);
        var export = new DragExportService(_context.Entries, _context.BlobStore, _context.Paths);
        var path = (await export.MaterializeAsync([item], _ct)).Single();
        Assert.Equal(remove ? null : Restricted, ReadZone(path));
    }

    [Fact]
    public async Task CreateAndUndoMustIncludeOrigin()
    {
        var item = await Add("a.txt", Download);
        var plan = await Plan(item);
        var operation = await _context.Executor.ExecuteAsync(plan, null, _ct);
        var target = plan.Entries.Single().TargetPath;
        Assert.Equal(Download, ReadZone(target));
        await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OverwriteAndUndoMustRestoreBothStreams(bool sourceMarked, bool targetMarked)
    {
        var item = await Add("a.txt", sourceMarked ? Download : null);
        var target = _context.Workspace.CreateFile("out/a.txt", "original");
        SetZone(target, targetMarked ? Restricted : null);
        var operation = await _context.Executor.ExecuteAsync(await Plan(item), null, _ct);
        Assert.Equal("snapshot", File.ReadAllText(target));
        Assert.Equal(sourceMarked ? Download : null, ReadZone(target));
        var undo = await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.False(undo.HasIssues);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal(targetMarked ? Restricted : null, ReadZone(target));
    }

    [Fact]
    public async Task IdenticalContentWithDifferentOriginMustBeApplied()
    {
        var item = await Add("a.txt", Download);
        var target = _context.Workspace.CreateFile("out/a.txt", "snapshot");
        var plan = await Plan(item);
        Assert.Equal(PlannedState.Overwrite, plan.Entries.Single().State);
        await _context.Executor.ExecuteAsync(plan, null, _ct);
        Assert.Equal(Download, ReadZone(target));
    }

    [Fact]
    public async Task PreviewMustRejectChangedOrigin()
    {
        var item = await Add("a.txt", Download);
        var target = _context.Workspace.CreateFile("out/a.txt", "original");
        var plan = await Plan(item);
        SetZone(target, Restricted);
        var operation = await _context.Executor.ExecuteAsync(plan, null, _ct);
        Assert.Equal(OperationStatus.PartiallyFailed, operation.Status);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal(Restricted, ReadZone(target));
    }

    [Fact]
    public async Task UndoMustPreserveExternallyEditedOrigin()
    {
        var item = await Add("a.txt", Download);
        var plan = await Plan(item);
        var operation = await _context.Executor.ExecuteAsync(plan, null, _ct);
        var target = plan.Entries.Single().TargetPath;
        SetZone(target, Restricted);
        var undo = await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.True(undo.HasIssues);
        Assert.True(File.Exists(target));
        Assert.Equal(Restricted, ReadZone(target));
    }

    [Fact]
    public async Task SourceStatusMustDetectOriginOnlyEdit()
    {
        var item = await Add("a.txt", Download);
        SetZone(item.SourcePath, Restricted);
        Assert.Equal(ItemSourceState.Modified,
            (await _context.Stash.CheckSourceStatusAsync(item.GroupId, _ct)).Single().State);
    }

    [Fact]
    public async Task RetryMustRetainSnapshotAndBackupOrigins()
    {
        var item = await Add("a.txt", Download);
        var target = _context.Workspace.CreateFile("out/a.txt", "original");
        SetZone(target, Restricted);
        Operation operation;
        using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            operation = await _context.Executor.ExecuteAsync(await Plan(item), null, _ct);
        Assert.Equal(1, await _context.Executor.RetryFailedAsync(operation.Id, _ct));
        Assert.Equal(Download, ReadZone(target));
        await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.Equal(Restricted, ReadZone(target));
    }

    [Fact]
    public async Task EmptyOriginMustRemainPresentThroughOverwriteAndUndo()
    {
        var item = await Add("a.txt", []);
        var target = _context.Workspace.CreateFile("out/a.txt", "original");
        SetZone(target, Download);
        var operation = await _context.Executor.ExecuteAsync(await Plan(item), null, _ct);
        Assert.NotNull(ReadZone(target));
        Assert.Empty(ReadZone(target)!);
        await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.Equal(Download, ReadZone(target));
    }

    [Fact]
    public async Task ReadOnlyTargetMustKeepAttributeAndRestoreOrigin()
    {
        var item = await Add("a.txt", null);
        var target = _context.Workspace.CreateFile("out/a.txt", "original");
        SetZone(target, Restricted);
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        var operation = await _context.Executor.ExecuteAsync(await Plan(item), null, _ct);
        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.Null(ReadZone(target));
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
        await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.Equal(Restricted, ReadZone(target));
        Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
    }

    private ApplyExecutor Executor(IAtomicFileWriter writer) => new(_context.BlobStore, _context.Operations,
        _context.OperationEntries, writer, new RestartManagerLockInspector(), _context.Settings);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryMustCompleteOriginRemovalAfterInterruptedReplace(bool undo)
    {
        var item = await Add("a.txt", undo ? Download : null);
        var target = _context.Workspace.CreateFile("out/a.txt", "original");
        SetZone(target, undo ? null : Restricted);
        var writer = new InterruptingWriter();
        var executor = Executor(writer);
        Operation operation;
        if (undo)
        {
            operation = await _context.Executor.ExecuteAsync(await Plan(item), null, _ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.UndoAsync(operation.Id, _ct));
        }
        else
        {
            var plan = await Plan(item);
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(plan, null, _ct));
            operation = (await _context.Operations.GetAllAsync(_ct)).First();
        }
        Assert.Empty(ReadZone(target)!);
        await _context.Executor.RecoverAsync(_ct);
        Assert.Null(ReadZone(target));
        await _context.Executor.RecoverAsync(_ct);
        var rows = await _context.OperationEntries.GetByOperationAsync(operation.Id, _ct);
        Assert.DoesNotContain(rows, row => row.Pending || row.UndoPending);
        if (!undo)
        {
            await _context.Executor.UndoAsync(operation.Id, _ct);
            Assert.Equal(Restricted, ReadZone(target));
        }
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Fact]
    public async Task RecoveryMustKeepExternalOriginEditUnresolved()
    {
        var item = await Add("a.txt", Download);
        var target = _context.Workspace.CreateFile("out/a.txt", "original");
        var plan = await Plan(item);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executor(new InterruptingWriter()).ExecuteAsync(plan, null, _ct));
        SetZone(target, Restricted);
        await _context.Executor.RecoverAsync(_ct);
        var operation = (await _context.Operations.GetAllAsync(_ct)).First();
        Assert.Contains(await _context.OperationEntries.GetByOperationAsync(operation.Id, _ct), row => row.Pending);
        Assert.Equal(Restricted, ReadZone(target));
    }

    [Fact]
    public async Task ApprovedRetryMustRejectChangedOrigin()
    {
        var item = await Add("a.txt", Download);
        var target = _context.Workspace.CreateFile("out/a.txt", "original");
        Operation operation;
        using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            operation = await _context.Executor.ExecuteAsync(await Plan(item), null, _ct);
        var approved = await _context.Executor.PrepareRetryAsync(operation.Id, _ct);
        SetZone(target, Restricted);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _context.Executor.RetryApprovedAsync(operation.Id, approved, _ct));
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal(Restricted, ReadZone(target));
    }


    [Fact]
    public async Task FolderExportMustPreserveEachChildOrigin()
    {
        var file = _context.Workspace.CreateFile("folder/sub/a.txt", "a");
        SetZone(file, Download);
        _context.Workspace.CreateFile("folder/b.txt", "b");
        var group = await _context.CreateGroupAsync();
        var item = (await _context.Stash.AddAsync(group, [Path.Combine(_context.Workspace.Root, "folder")], null, _ct)).Added.Single();
        var path = (await new DragExportService(_context.Entries, _context.BlobStore, _context.Paths)
            .MaterializeAsync([item], _ct)).Single();
        Assert.Equal(Download, ReadZone(Path.Combine(path, "sub/a.txt")));
        Assert.Null(ReadZone(Path.Combine(path, "b.txt")));
    }

    [Fact]
    public async Task FrozenExecutionMustDetachOriginBytes()
    {
        var first = await Add("a.txt", Download);
        var second = await Add("b.txt", Download);
        var plan = await _context.Planner.BuildPlanAsync([first, second],
            new TargetSpec(TargetMode.SpecifiedPath, Path.Combine(_context.Workspace.Root, "out"), StructureMode.Flatten),
            ConflictPolicy.Overwrite, _ct);
        await _context.Executor.ExecuteAsync(plan,
            new InlineProgress(info => { if (info.Done == 1) plan.Entries[1].Source.ZoneIdentifier![0] = 0; }), _ct);
        Assert.Equal(Download, ReadZone(plan.Entries[1].TargetPath));
    }

    [Fact]
    public async Task FailedTemporaryOriginWriteMustPreserveTarget()
    {
        var item = await Add("a.txt", Download);
        var target = _context.Workspace.CreateFile("out/a.txt", "original");
        SetZone(target, Restricted);
        using var blobs = new LockedMetadataBlobStore(_context.BlobStore);
        var executor = new ApplyExecutor(blobs, _context.Operations, _context.OperationEntries,
            new AtomicFileWriter(), new RestartManagerLockInspector(), _context.Settings);
        var operation = await executor.ExecuteAsync(await Plan(item), null, _ct);
        Assert.Equal(OperationStatus.PartiallyFailed, operation.Status);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal(Restricted, ReadZone(target));
    }

    private sealed class InlineProgress(Action<ProgressInfo> report) : IProgress<ProgressInfo>
    {
        public void Report(ProgressInfo info) => report(info);
    }

    private sealed class LockedMetadataBlobStore(IBlobStore inner) : IBlobStore, IDisposable
    {
        private FileStream? _lock;
        public Task<(string Sha256, long Size)> PutFileAsync(string path, CancellationToken ct) => inner.PutFileAsync(path, ct);
        public string GetBlobPath(string sha256) => inner.GetBlobPath(sha256);
        public bool Exists(string sha256) => inner.Exists(sha256);
        public long GetTotalSize() => inner.GetTotalSize();
        public Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referenced, CancellationToken ct) =>
            inner.DeleteUnreferencedAsync(referenced, ct);
        public async Task CopyBlobToAsync(string sha256, string destinationPath, CancellationToken ct)
        {
            await inner.CopyBlobToAsync(sha256, destinationPath, ct);
            _lock = File.Open(destinationPath + ":Zone.Identifier", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        }
        public void Dispose() => _lock?.Dispose();
    }

    private sealed class InterruptingWriter : IAtomicFileWriter
    {
        private readonly AtomicFileWriter _inner = new();
        public string? GetIdentity(string path) => _inner.GetIdentity(path);
        public IDisposable? GuardTarget(string path) => _inner.GuardTarget(path);
        public bool DeleteDirectory(string path, string expectedIdentity) => _inner.DeleteDirectory(path, expectedIdentity);
        public Task<bool> CreateAsync(string temporaryPath, string targetPath, CancellationToken ct) =>
            _inner.CreateAsync(temporaryPath, targetPath, ct);
        public async Task<bool> ReplaceAsync(string temporaryPath, string targetPath, CancellationToken ct)
        {
            await _inner.ReplaceAsync(temporaryPath, targetPath, ct);
            throw new InvalidOperationException("Interrupted between file replacement and origin removal");
        }
    }
}
