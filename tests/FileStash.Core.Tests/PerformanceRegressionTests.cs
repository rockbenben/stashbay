
using Dapper;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Infrastructure.Data;
using FileStash.Infrastructure.Native;
using Microsoft.Data.Sqlite;

namespace FileStash.Core.Tests;

public sealed class PerformanceRegressionTests
{
    [Fact]
    public async Task CompletedHistoryMustRequireOnlyOneRecoveryQuery()
    {
        using var context = new ApplyTestContext();
        var ct = TestContext.Current.CancellationToken;
        await context.Migrator.MigrateAsync(ct);
        for (var index = 0; index < 20; index++)
        {
            var operation = new Operation { Id = index.ToString(), Status = OperationStatus.Succeeded, TargetDescription = "fixture" };
            await context.Operations.AddAsync(operation, ct);
            await context.OperationEntries.AddManyAsync([new OperationEntry { OperationId = operation.Id }], ct);
        }
        var counting = new CountingFactory(context.Workspace.ConnectionFactory);
        var executor = new ApplyExecutor(context.BlobStore, new OperationRepository(counting),
            new OperationEntryRepository(counting), new AtomicFileWriter(), new RestartManagerLockInspector(), context.Settings);
        Assert.Equal(0, await executor.RecoverAsync(ct));
        Assert.Equal(1, counting.Count);
    }

    [Fact]
    public async Task RecoveryCandidatesMustIncludeRunningAndUnresolvedOperationsOnly()
    {
        using var context = new ApplyTestContext();
        var ct = TestContext.Current.CancellationToken;
        await context.Migrator.MigrateAsync(ct);
        using var connection = await context.Workspace.ConnectionFactory.OpenAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO Operation(Id,StartedAt,TargetMode,TargetDescription,Status) VALUES
            ('done','2026-01-01',2,'fixture',1),
            ('running','2026-01-01',2,'fixture',0),
            ('pending','2026-01-01',2,'fixture',1),
            ('undo','2026-01-01',2,'fixture',2),
            ('undone','2026-01-01',2,'fixture',5);
            INSERT INTO OperationEntry(OperationId,TargetPath,Action,Pending,UndoPending) VALUES
            ('done','unused',0,0,0),('pending','unused',0,1,0),
            ('undo','unused',0,0,1),('undone','unused',0,1,1);
            """);
        var ids = await context.Operations.GetRecoveryCandidateIdsAsync(ct);
        Assert.Equal(new[] { "pending", "running", "undo" }, ids.Order());
        await context.Executor.RecoverAsync(ct);
        Assert.Equal(OperationStatus.Cancelled, (await context.Operations.GetAsync("running", ct))!.Status);
        Assert.True((await context.OperationEntries.GetByOperationAsync("pending", ct)).Single().Pending);
        Assert.Equal(OperationStatus.Undone, (await context.Operations.GetAsync("undo", ct))!.Status);
        Assert.True((await context.OperationEntries.GetByOperationAsync("undone", ct)).Single().Pending);
    }

    [Fact]
    public void DirectoryEnumerationMustHonorCancellation()
    {
        using var workspace = new TempWorkspace();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            FolderEnumerator.Enumerate(workspace.Root, workspace.Root, ct: cancellation.Token));
    }

    [Fact]
    public void DirectoryEnumerationMustKeepOnlyEmptyLeavesAfterExclusions()
    {
        using var workspace = new TempWorkspace();
        workspace.CreateFile("tree/with/a.txt", "a");
        workspace.CreateFile("tree/with/sub/b.txt", "b");
        workspace.CreateFile("tree/filtered/a.tmp", "excluded");
        workspace.CreateFile("tree/.git/config", "excluded");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "tree/empty/child"));
        Directory.CreateDirectory(Path.Combine(workspace.Root, "tree/empty-more"));
        var data = FolderEnumerator.Enumerate(workspace.Root, Path.Combine(workspace.Root, "tree"), ["**/*.tmp"], TestContext.Current.CancellationToken);
        Assert.Empty(data.Warnings);
        Assert.Equal(new[] { @"tree\empty-more", @"tree\empty\child", @"tree\filtered" },
            data.Entries.Where(entry => entry.IsDirectory).Select(entry => entry.RelativePath).Order());
        Assert.Equal(new[] { @"tree\with\a.txt", @"tree\with\sub\b.txt" },
            data.Entries.Where(entry => !entry.IsDirectory).Select(entry => entry.RelativePath).Order());
    }


    [Fact]
    public async Task AsyncDirectoryEnumerationMustMatchSynchronousOutput()
    {
        using var workspace = new TempWorkspace();
        workspace.CreateFile("tree/with/a.txt", "a");
        workspace.CreateFile("tree/with/ignored.tmp", "ignored");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "tree/empty/leaf"));
        var folder = Path.Combine(workspace.Root, "tree");
        var ct = TestContext.Current.CancellationToken;
        var expected = FolderEnumerator.Enumerate(workspace.Root, folder, ["**/*.tmp"], ct);
        var actual = await FolderEnumerator.EnumerateAsync(workspace.Root, folder, ["**/*.tmp"], ct);
        Assert.Equal(expected.Entries, actual.Entries);
        Assert.Equal(expected.Warnings, actual.Warnings);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FolderEnumerator.EnumerateAsync(workspace.Root, folder, ct: cancellation.Token));
    }

    private sealed class CountingFactory(ISqliteConnectionFactory inner) : ISqliteConnectionFactory
    {
        public int Count { get; private set; }
        public Task<SqliteConnection> OpenAsync(CancellationToken ct)
        {
            Count++;
            return inner.OpenAsync(ct);
        }
    }
}
