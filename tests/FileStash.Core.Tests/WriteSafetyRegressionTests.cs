using Dapper;
using FileStash.Core.Abstractions;
using FileStash.Infrastructure.Data;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Infrastructure.Native;

namespace FileStash.Core.Tests;

public sealed class WriteSafetyRegressionTests : IAsyncLifetime
{
    private readonly ApplyTestContext _context = new();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _context.Migrator.MigrateAsync(_ct);
    public ValueTask DisposeAsync() { _context.Dispose(); return ValueTask.CompletedTask; }

    private async Task<StashItem> AddAsync(string path, string content)
    {
        var group = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile(path, content);
        return (await _context.Stash.AddAsync(group, [source], null, _ct)).Added.Single();
    }

    private Task<ApplyPlan> PlanAsync(params StashItem[] items) => _context.Planner.BuildPlanAsync(
        items, new TargetSpec(TargetMode.SpecifiedPath, Path.Combine(_context.Workspace.Root, "out"), StructureMode.Flatten),
        ConflictPolicy.Ask, _ct);

    private sealed class CountingProgress(Action<ProgressInfo> report) : IProgress<ProgressInfo>
    {
        public void Report(ProgressInfo value) => report(value);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("remove")]
    [InlineData("delete-group")]
    public async Task MetadataMutationMustWaitForActiveStorageWriter(string action)
    {
        var item = await AddAsync("source/a.txt", "a");
        var targetGroup = await _context.CreateGroupAsync();
        Task mutation;
        bool completed;
        using (File.Open(Path.Combine(_context.Paths.RootDirectory, "write.lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            mutation = action switch
            {
                "move" => _context.Stash.MoveItemsAsync([item.Id], targetGroup),
                "remove" => _context.Stash.RemoveItemsAsync([item.Id]),
                _ => _context.Stash.DeleteGroupAsync(item.GroupId)
            };
            completed = mutation.IsCompleted;
        }
        await mutation;
        Assert.False(completed);
    }

    [Fact]
    public async Task EmptyDirectoryApplyMustRejectJunctionAncestor()
    {
        var source = Path.Combine(_context.Workspace.Root, "source/box/empty");
        Directory.CreateDirectory(source);
        var group = await _context.CreateGroupAsync();
        var folder = Path.GetDirectoryName(source)!;
        var item = (await _context.Stash.AddAsync(group, [folder], null, _ct)).Added.Single();
        var outside = Path.Combine(_context.Workspace.Root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(_context.Workspace.Root, "out"));
        var junction = Path.Combine(_context.Workspace.Root, "out/box");
        var start = new System.Diagnostics.ProcessStartInfo("pwsh")
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:TEST_LINK -Target $env:TEST_TARGET | Out-Null");
        start.Environment["TEST_LINK"] = junction;
        start.Environment["TEST_TARGET"] = outside;
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync(_ct);
        Assert.Equal(0, process.ExitCode);
        try
        {
            var plan = await _context.Planner.BuildPlanAsync([item],
                new TargetSpec(TargetMode.SpecifiedPath, Path.Combine(_context.Workspace.Root, "out"), StructureMode.ItemOnly),
                ConflictPolicy.Overwrite, _ct);
            var operation = await _context.Executor.ExecuteAsync(plan, null, _ct);
            Assert.Equal(OperationStatus.PartiallyFailed, operation.Status);
            Assert.False(Directory.Exists(Path.Combine(outside, "empty")));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public async Task IdenticalOverwriteMustRejectEditAfterPreview()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var target = _context.Workspace.CreateFile("out/report.txt", "snapshot");
        var plan = await _context.Planner.BuildPlanAsync([item],
            new TargetSpec(TargetMode.SpecifiedPath, Path.GetDirectoryName(target), StructureMode.Flatten),
            ConflictPolicy.Overwrite, _ct, skipIdentical: false);
        File.WriteAllText(target, "edited after preview");
        var operation = await _context.Executor.ExecuteAsync(plan, null, _ct);
        Assert.Equal("edited after preview", File.ReadAllText(target));
        Assert.Equal(OperationStatus.PartiallyFailed, operation.Status);
    }

    [Fact]
    public async Task ExecutingPlanMustIgnoreLaterPreviewChanges()
    {
        var first = await AddAsync("source/a.txt", "a");
        var second = await AddAsync("source/b.txt", "b");
        _context.Workspace.CreateFile("out/b.txt", "old");
        var plan = await PlanAsync(first, second);
        var progress = new CountingProgress(info =>
        {
            if (info.Done == 1)
            {
                plan.Entries[1].Action = PlannedAction.Rename;
                plan.Entries[1].Source.Sha256 = plan.Entries[0].Source.Sha256;
            }
        });
        var operation = await _context.Executor.ExecuteAsync(plan, progress, _ct);
        Assert.Equal("b", File.ReadAllText(Path.Combine(_context.Workspace.Root, "out/b.txt")));
        Assert.False(File.Exists(Path.Combine(_context.Workspace.Root, "out/b (2).txt")));
        await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.Equal("old", File.ReadAllText(Path.Combine(_context.Workspace.Root, "out/b.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshMustPersistChangedSourceType(bool startsAsDirectory)
    {
        var source = Path.Combine(_context.Workspace.Root, "source", "shape");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        if (startsAsDirectory)
        {
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "old.txt"), "old");
        }
        else File.WriteAllText(source, "old");
        var group = await _context.CreateGroupAsync();
        var item = (await _context.Stash.AddAsync(group, [source], null, _ct)).Added.Single();
        if (startsAsDirectory) Directory.Delete(source, true);
        else File.Delete(source);
        if (startsAsDirectory) File.WriteAllText(source, "new");
        else
        {
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "a.txt"), "a");
            File.WriteAllText(Path.Combine(source, "b.txt"), "b");
        }
        await _context.Stash.RefreshSnapshotAsync([item.Id], null, _ct);
        item = (await _context.Items.GetByIdsAsync([item.Id], _ct)).Single();
        Assert.Equal(startsAsDirectory ? StashItemKind.File : StashItemKind.Folder, item.Kind);
        var export = new DragExportService(_context.Entries, _context.BlobStore, _context.Paths);
        var path = (await export.MaterializeAsync([item], _ct)).Single();
        if (startsAsDirectory) Assert.Equal("new", File.ReadAllText(path));
        else
        {
            Assert.Equal("a", File.ReadAllText(Path.Combine(path, "a.txt")));
            Assert.Equal("b", File.ReadAllText(Path.Combine(path, "b.txt")));
        }
    }

    [Fact]
    public async Task ExportMustRejectInconsistentFileSnapshot()
    {
        var item = await AddAsync("source/a.txt", "a");
        var entry = (await _context.Entries.GetByItemAsync(item.Id, _ct)).Single();
        entry.RelativePath = "source/b.txt";
        entry.Id = 0;
        await _context.Entries.AddManyAsync([entry], _ct);
        var export = new DragExportService(_context.Entries, _context.BlobStore, _context.Paths);
        await Assert.ThrowsAsync<IOException>(() => export.MaterializeAsync([item], _ct));
    }

    [Fact]
    public async Task LockedUndoMustContinueWithOtherFiles()
    {
        var a = await AddAsync("source/a.txt", "a");
        var b = await AddAsync("source/b.txt", "b");
        var operation = await _context.Executor.ExecuteAsync(await PlanAsync(a, b), null, _ct);
        using (File.Open(Path.Combine(_context.Workspace.Root, "out/b.txt"),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await _context.Executor.UndoAsync(operation.Id, _ct);
            Assert.True(result.HasIssues);
            Assert.False(File.Exists(Path.Combine(_context.Workspace.Root, "out/a.txt")));
        }
        await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.False(File.Exists(Path.Combine(_context.Workspace.Root, "out/b.txt")));
    }

    [Fact]
    public async Task DirectoryUndoMustPreserveReplacementDirectory()
    {
        var source = Path.Combine(_context.Workspace.Root, "empty");
        Directory.CreateDirectory(source);
        var group = await _context.CreateGroupAsync();
        var item = (await _context.Stash.AddAsync(group, [source], null, _ct)).Added.Single();
        var operation = await _context.Executor.ExecuteAsync(await PlanAsync(item), null, _ct);
        var target = Path.Combine(_context.Workspace.Root, "out/empty");
        Directory.Move(target, target + "-original");
        Directory.CreateDirectory(target);
        var result = await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.True(Directory.Exists(target));
        Assert.True(result.HasIssues);
    }

    [Fact]
    public async Task PreviewRenameMustPreserveOriginalThroughUndo()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var original = _context.Workspace.CreateFile("out/report.txt", "original");
        var plan = await PlanAsync(item);
        plan.Entries.Single().Action = PlannedAction.Rename;
        var op = await _context.Executor.ExecuteAsync(plan, null, _ct);
        Assert.Equal("original", File.ReadAllText(original));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(original)!).Length);
        await _context.Executor.UndoAsync(op.Id, _ct);
        Assert.Equal("original", File.ReadAllText(original));
    }

    [Fact]
    public async Task FileAppearingAfterPlanningMustSurviveCreateAndUndo()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var plan = await PlanAsync(item);
        var original = _context.Workspace.CreateFile("out/report.txt", "appeared");
        var op = await _context.Executor.ExecuteAsync(plan, null, _ct);
        Assert.Equal("appeared", File.ReadAllText(original));
        await _context.Executor.UndoAsync(op.Id, _ct);
        Assert.Equal("appeared", File.ReadAllText(original));
    }

    [Fact]
    public async Task ChangedOverwriteTargetMustBeReconfirmed()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var original = _context.Workspace.CreateFile("out/report.txt", "original");
        var plan = await PlanAsync(item);
        File.WriteAllText(original, "external edit after preview");
        var operation = await _context.Executor.ExecuteAsync(plan, null, _ct);
        Assert.Equal(OperationStatus.PartiallyFailed, operation.Status);
        Assert.Equal("external edit after preview", File.ReadAllText(original));
    }

    [Fact]
    public async Task ExistingTemporaryNamesMustSurviveApplyAndUndo()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var original = _context.Workspace.CreateFile("out/report.txt", "original");
        var scratch = _context.Workspace.CreateFile("out/.report.txt.fstmp~", "user temp");
        var undoScratch = _context.Workspace.CreateFile("out/.report.txt.undo~", "user undo");
        var op = await _context.Executor.ExecuteAsync(await PlanAsync(item), null, _ct);
        await _context.Executor.UndoAsync(op.Id, _ct);
        Assert.Equal("original", File.ReadAllText(original));
        Assert.Equal("user temp", File.ReadAllText(scratch));
        Assert.Equal("user undo", File.ReadAllText(undoScratch));
    }

    [Fact]
    public async Task CancellationInsideSecondWriteMustLeaveFirstUndoable()
    {
        var first = await AddAsync("source/a.txt", "first");
        var second = await AddAsync("source/b.txt", "second");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var executor = new ApplyExecutor(_context.BlobStore, _context.Operations, _context.OperationEntries,
            new CancelSecondWriter(cancel), new RestartManagerLockInspector(), _context.Settings);
        var op = await executor.ExecuteAsync(await PlanAsync(first, second), null, cancel.Token);
        Assert.Equal(OperationStatus.Cancelled, op.Status);
        Assert.Contains(await _context.OperationEntries.GetByOperationAsync(op.Id, _ct), x => x.Action == EntryResult.Created);
        await executor.UndoAsync(op.Id, _ct);
        Assert.False(File.Exists(Path.Combine(_context.Workspace.Root, "out/a.txt")));
    }

    [Fact]
    public async Task LockedRefreshMustKeepPreviousSnapshot()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var before = (await _context.Entries.GetByItemAsync(item.Id, _ct)).Single().Sha256;
        using (File.Open(item.SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try { await _context.Stash.RefreshSnapshotAsync([item.Id], null, _ct); }
            catch (IOException) { }
        }
        Assert.Equal(before, (await _context.Entries.GetByItemAsync(item.Id, _ct)).Single().Sha256);
    }

    [Fact]
    public async Task ExportMustKeepBothSameNamedFiles()
    {
        var a = await AddAsync("one/report.txt", "first");
        var b = await AddAsync("two/report.txt", "second");
        var export = new DragExportService(_context.Entries, _context.BlobStore, _context.Paths);
        var paths = await export.MaterializeAsync([a, b], _ct);
        Assert.Equal(2, paths.Count);
        Assert.Equal(new[] { "first", "second" }, paths.Select(File.ReadAllText).Order());
    }

    [Fact]
    public async Task RetryOverwriteMustRestoreOriginalOnUndo()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var original = _context.Workspace.CreateFile("out/report.txt", "original");
        var plan = await PlanAsync(item);
        Operation op;
        using (File.Open(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            op = await _context.Executor.ExecuteAsync(plan, null, _ct);
        Assert.Equal(1, await _context.Executor.RetryFailedAsync(op.Id, _ct));
        await _context.Executor.UndoAsync(op.Id, _ct);
        Assert.Equal("original", File.ReadAllText(original));
    }

    [Fact]
    public async Task SnapshotTransactionMustRollBackMetadataAndEntriesTogether()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var before = (await _context.Entries.GetByItemAsync(item.Id, _ct)).Single();
        item.TotalSize = 999;
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            _context.Items.SaveSnapshotAsync(item,
                [new StashEntry { ItemId = "missing", RelativePath = "bad" }], false, _ct));
        Assert.Equal(before.Sha256, (await _context.Entries.GetByItemAsync(item.Id, _ct)).Single().Sha256);
        Assert.NotEqual(999, (await _context.Items.GetByIdsAsync([item.Id], _ct)).Single().TotalSize);
    }

    [Fact]
    public async Task UpgradeFromV1MustKeepLegacyHistoryAndBackup()
    {
        using var workspace = new TempWorkspace();
        await using (var connection = await workspace.ConnectionFactory.OpenAsync(_ct))
        {
            await connection.ExecuteAsync(Migrations.All[0].Sql);
            await connection.ExecuteAsync("""
                CREATE TABLE SchemaVersion (Version INTEGER NOT NULL);
                INSERT INTO SchemaVersion VALUES (1);
                INSERT INTO Operation VALUES ('old', '2026-01-01T00:00:00.0000000+00:00', NULL, NULL, 0, 'old', 2, NULL);
                INSERT INTO OperationEntry (OperationId, TargetPath, Action, NewSha256)
                VALUES ('old', 'legacy.txt', 4, 'legacyhash');
                """);
        }
        Assert.Equal(Migrations.All.Max(migration => migration.Version), await new DatabaseMigrator(workspace.ConnectionFactory).MigrateAsync(_ct));
        var entry = Assert.Single(await new OperationEntryRepository(workspace.ConnectionFactory).GetByOperationAsync("old", _ct));
        Assert.Null(entry.IntendedAction);
        Assert.False(entry.Pending);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(workspace.Paths.DatabasePath)!, "*.schema-v1-*.bak"));
    }

    [Fact]
    public async Task InterruptedCommitMustKeepDurablePendingIntentAndNeverDeleteTarget()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var executor = new ApplyExecutor(_context.BlobStore, _context.Operations, _context.OperationEntries,
            new InterruptAfterCommitWriter(), new RestartManagerLockInspector(), _context.Settings);
        var plan = await PlanAsync(item);
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(plan, null, _ct));
        var operation = (await _context.Operations.GetAllAsync(_ct)).Single();
        var pending = Assert.Single(await _context.OperationEntries.GetByOperationAsync(operation.Id, _ct), entry => entry.Pending);
        Assert.Equal(PlannedAction.Create, pending.IntendedAction);
        Assert.Equal("snapshot", File.ReadAllText(pending.TargetPath));
        Assert.Equal(0, await executor.RetryFailedAsync(operation.Id, _ct));
        await executor.UndoAsync(operation.Id, _ct);
        Assert.Equal("snapshot", File.ReadAllText(pending.TargetPath));
    }

    [Fact]
    public async Task ExportRootEmptyFolderMustCreateNamedDirectory()
    {
        var group = await _context.CreateGroupAsync();
        var folder = Directory.CreateDirectory(Path.Combine(_context.Workspace.Root, "empty")).FullName;
        var item = (await _context.Stash.AddAsync(group, [folder], null, _ct)).Added.Single();
        var paths = await new DragExportService(_context.Entries, _context.BlobStore, _context.Paths).MaterializeAsync([item], _ct);
        Assert.Equal("empty", Path.GetFileName(Assert.Single(paths)));
        Assert.True(Directory.Exists(paths[0]));
    }

    [Fact]
    public async Task CollectionMustWaitForSnapshotReferenceCommit()
    {
        var group = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile("source/report.txt", "snapshot");
        var cleanup = new CleanupService(_context.Entries, _context.OperationEntries,
            _context.Operations, _context.BlobStore, _context.Paths);
        Task<int>? collection = null;
        var progress = new InlineProgress(() =>
        {
            collection ??= cleanup.DeleteUnreferencedBlobsAsync(_ct);
        });
        var result = await _context.Stash.AddAsync(group, [source], progress, _ct);
        Assert.NotNull(collection);
        Assert.Equal(0, await collection);
        var entry = (await _context.Entries.GetByItemAsync(result.Added.Single().Id, _ct)).Single();
        Assert.True(_context.BlobStore.Exists(entry.Sha256!));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExpiryMustRetainUnresolvedCommitEvidence(bool pending, bool undoPending)
    {
        var operation = new Operation
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.UtcNow.AddDays(-60),
            Status = OperationStatus.PartiallyFailed,
        };
        await _context.Operations.AddAsync(operation, _ct);
        await _context.OperationEntries.AddManyAsync(
            [new OperationEntry { OperationId = operation.Id, Pending = pending, UndoPending = undoPending, Action = EntryResult.Skipped }], _ct);
        var cleanup = new CleanupService(_context.Entries, _context.OperationEntries,
            _context.Operations, _context.BlobStore, _context.Paths);
        Assert.Equal(0, await cleanup.DeleteExpiredHistoryAsync(30, _ct));
        Assert.NotNull(await _context.Operations.GetAsync(operation.Id, _ct));
    }

    [Fact]
    public async Task ApprovedRetryMustRejectChangedJournalBeforeWriting()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var original = _context.Workspace.CreateFile("out/report.txt", "original");
        Operation operation;
        using (File.Open(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            operation = await _context.Executor.ExecuteAsync(await PlanAsync(item), null, _ct);
        var approved = await _context.Executor.PrepareRetryAsync(operation.Id, _ct);
        var row = Assert.Single(await _context.OperationEntries.GetByOperationAsync(operation.Id, _ct),
            entry => entry.Action == EntryResult.Failed);
        row.TargetPath = Path.Combine(_context.Workspace.Root, "unapproved.txt");
        await _context.OperationEntries.UpdateAsync(row, _ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _context.Executor.RetryApprovedAsync(operation.Id, approved, _ct));
        Assert.Equal("original", File.ReadAllText(original));
        Assert.False(File.Exists(row.TargetPath));
    }

    [Fact]
    public async Task ApprovedRetryMustRejectChangedTarget()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var original = _context.Workspace.CreateFile("out/report.txt", "original");
        Operation operation;
        using (File.Open(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            operation = await _context.Executor.ExecuteAsync(await PlanAsync(item), null, _ct);
        var approved = await _context.Executor.PrepareRetryAsync(operation.Id, _ct);
        File.WriteAllText(original, "edited after approval");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _context.Executor.RetryApprovedAsync(operation.Id, approved, _ct));
        Assert.Equal("edited after approval", File.ReadAllText(original));
    }

    [Fact]
    public async Task CorruptBackupMustNotReplaceAppliedContentDuringUndo()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var target = _context.Workspace.CreateFile("out/report.txt", "original");
        var operation = await _context.Executor.ExecuteAsync(await PlanAsync(item), null, _ct);
        var row = (await _context.OperationEntries.GetByOperationAsync(operation.Id, _ct))
            .Single(entry => entry.Action == EntryResult.Overwritten);
        var backup = _context.BlobStore.GetBlobPath(row.BackupSha256!);
        File.SetAttributes(backup, FileAttributes.Normal);
        File.WriteAllText(backup, "corrupted backup");
        var result = await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.True(result.HasIssues);
        Assert.Equal("snapshot", File.ReadAllText(target));
    }

    [Fact]
    public async Task CorruptBlobMustNotBeWrittenToApprovedTarget()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var original = _context.Workspace.CreateFile("out/report.txt", "original");
        Operation operation;
        using (File.Open(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            operation = await _context.Executor.ExecuteAsync(await PlanAsync(item), null, _ct);
        var approved = await _context.Executor.PrepareRetryAsync(operation.Id, _ct);
        var blob = _context.BlobStore.GetBlobPath(Assert.Single(approved).Sha256);
        File.SetAttributes(blob, FileAttributes.Normal);
        File.WriteAllText(blob, "corrupt");
        Assert.Equal(0, await _context.Executor.RetryApprovedAsync(operation.Id, approved, _ct));
        Assert.Equal("original", File.ReadAllText(original));
    }

    [Fact]
    public async Task UnknownPendingContentMustRequireExplicitKeepDecision()
    {
        var file = _context.Workspace.CreateFile("out/report.txt", "external");
        var operation = new Operation { Id = Guid.NewGuid().ToString("N"), Status = OperationStatus.Running };
        await _context.Operations.AddAsync(operation, _ct);
        var row = new OperationEntry
        {
            OperationId = operation.Id, TargetPath = file, Action = EntryResult.Skipped,
            NewSha256 = FileStash.Core.Utils.HashUtil.ComputeSha256(System.Text.Encoding.UTF8.GetBytes("external")),
            Pending = true, IntendedAction = PlannedAction.Create,
        };
        await _context.OperationEntries.AddManyAsync([row], _ct);
        await _context.Executor.RecoverAsync(_ct);
        Assert.True((await _context.OperationEntries.GetByOperationAsync(operation.Id, _ct)).Single().Pending);
        await _context.Executor.KeepCurrentAsync(operation.Id, row.Id, _ct);
        await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.Equal("external", File.ReadAllText(file));
        Assert.False((await _context.OperationEntries.GetByOperationAsync(operation.Id, _ct)).Single().Pending);
    }

    [Fact]
    public async Task UndoMustPreserveExternallyReplacedFileEvenWithSameBytes()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var operation = await _context.Executor.ExecuteAsync(await PlanAsync(item), null, _ct);
        var target = Path.Combine(_context.Workspace.Root, "out/report.txt");
        File.Move(target, target + ".saved");
        File.WriteAllText(target, "snapshot");
        var result = await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.True(result.HasIssues);
        Assert.Equal("snapshot", File.ReadAllText(target));
    }

    [Fact]
    public async Task V2PendingUpgradeMustRemainUnresolvedUntilExplicitDecision()
    {
        using var workspace = new TempWorkspace();
        await using (var connection = await workspace.ConnectionFactory.OpenAsync(_ct))
        {
            await connection.ExecuteAsync(Migrations.All[0].Sql);
            await connection.ExecuteAsync(Migrations.All[1].Sql);
            await connection.ExecuteAsync("""
                CREATE TABLE SchemaVersion (Version INTEGER NOT NULL);
                INSERT INTO SchemaVersion VALUES (1), (2);
                INSERT INTO Operation VALUES ('v2', '2026-01-01T00:00:00.0000000+00:00', NULL, NULL, 0, 'old', 0, NULL);
                INSERT INTO OperationEntry (OperationId, TargetPath, Action, Pending, IntendedAction)
                VALUES ('v2', 'legacy.txt', 3, 1, 0);
                """);
        }
        Assert.Equal(Migrations.All.Max(migration => migration.Version), await new DatabaseMigrator(workspace.ConnectionFactory).MigrateAsync(_ct));
        var row = Assert.Single(await new OperationEntryRepository(workspace.ConnectionFactory).GetByOperationAsync("v2", _ct));
        Assert.True(row.Pending);
        Assert.Null(row.WitnessIdentity);
        Assert.Null(row.WitnessPath);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(workspace.Paths.DatabasePath)!, "*.schema-v2-*.bak"));
    }

    [Fact]
    public async Task ElevatedScopePreviewMustNotReadTargetBeforeConsent()
    {
        var item = await AddAsync("source/report.txt", "snapshot");
        var original = _context.Workspace.CreateFile("out/report.txt", "original");
        Operation operation;
        IReadOnlyList<RetryEntry> scope;
        using (File.Open(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            operation = await _context.Executor.ExecuteAsync(await PlanAsync(item), null, _ct);
            scope = await _context.Executor.PrepareRetryAsync(operation.Id, _ct, inspectTargets: false);
            Assert.False(Assert.Single(scope).TargetInspected);
        }
        Assert.Equal(1, await _context.Executor.RetryApprovedAsync(operation.Id, scope, _ct));
        await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.Equal("original", File.ReadAllText(original));
    }

    [Fact]
    public async Task UnstartedEntriesMustSurviveCancellationAndBlobCollection()
    {
        var first = await AddAsync("source/a.txt", "first");
        var second = await AddAsync("source/b.txt", "second");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var operation = await _context.Executor.ExecuteAsync(await PlanAsync(first, second),
            new InlineProgress(() => cancellation.Cancel()), cancellation.Token);
        var unstarted = Assert.Single(await _context.OperationEntries.GetByOperationAsync(operation.Id, _ct),
            entry => entry.Action == EntryResult.Failed);
        Assert.Equal(PlannedAction.Create, unstarted.IntendedAction);
        await _context.Stash.RemoveItemsAsync([first.Id, second.Id]);
        var cleanup = new CleanupService(_context.Entries, _context.OperationEntries,
            _context.Operations, _context.BlobStore, _context.Paths);
        await cleanup.DeleteUnreferencedBlobsAsync(_ct);
        Assert.True(_context.BlobStore.Exists(unstarted.NewSha256!));
        Assert.Equal(1, await _context.Executor.RetryFailedAsync(operation.Id, _ct));
        Assert.Equal("second", File.ReadAllText(unstarted.TargetPath));
        await _context.Executor.UndoAsync(operation.Id, _ct);
        Assert.False(Directory.Exists(Path.Combine(_context.Workspace.Root, "out")));
    }

    private sealed class InlineProgress(Action report) : IProgress<ProgressInfo>
    {
        public void Report(ProgressInfo value) => report();
    }

    private sealed class InterruptAfterCommitWriter : IAtomicFileWriter
    {
        public Task<bool> CreateAsync(string temporaryPath, string targetPath, CancellationToken ct)
        {
            File.Move(temporaryPath, targetPath);
            throw new InvalidOperationException("Injected interruption after file commit");
        }
        public Task<bool> ReplaceAsync(string temporaryPath, string targetPath, CancellationToken ct) =>
            throw new InvalidOperationException();
    }

    private sealed class CancelSecondWriter(CancellationTokenSource cancel) : IAtomicFileWriter
    {
        private int _count;
        public Task<bool> CreateAsync(string temporaryPath, string targetPath, CancellationToken ct) =>
            ReplaceAsync(temporaryPath, targetPath, ct);

        public Task<bool> ReplaceAsync(string temporaryPath, string targetPath, CancellationToken ct)
        {
            if (++_count == 2) cancel.Cancel();
            return new AtomicFileWriter().ReplaceAsync(temporaryPath, targetPath, ct);
        }
    }
}
