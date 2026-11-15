using Dapper;
using FileStash.Infrastructure.Data;
using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.App.Services;

namespace FileStash.Core.Tests;

public sealed class ShelfLifetimeTests
{
    [Fact]
    public async Task ClearingOneShelfPreservesOtherWaitingWindowsAndCreationReservations()
    {
        using var context = new ApplyTestContext();
        var ct = TestContext.Current.CancellationToken;
        await context.Migrator.MigrateAsync(ct);
        var lifetime = new ShelfLifetimeService(context.Groups, context.Stash);
        var clearing = await lifetime.CreateTemporaryAsync(new object(), "clear this");
        var waitingWindow = new object();
        var waiting = await lifetime.CreateTemporaryAsync(waitingWindow, "waiting for drop");
        var pendingReservation = new object();
        var pending = await lifetime.CreateTemporaryAsync(pendingReservation, "not shown yet");
        var file = context.Workspace.CreateFile("one-window.txt", "keep source");
        var item = Assert.Single((await context.Stash.AddAsync(clearing.Id, [file], null, ct)).Added);

        await context.Stash.RemoveItemsAsync([item.Id]);
        Assert.Null(await context.Groups.GetAsync(clearing.Id, ct));
        Assert.Equal(0, await lifetime.DeleteEmptyAsync(ct));
        Assert.NotNull(await context.Groups.GetAsync(waiting.Id, ct));
        Assert.NotNull(await context.Groups.GetAsync(pending.Id, ct));

        await lifetime.SetWindowGroupAsync(waitingWindow, null);
        Assert.Null(await context.Groups.GetAsync(waiting.Id, ct));
        Assert.NotNull(await context.Groups.GetAsync(pending.Id, ct));
        var shownWindow = new object();
        await lifetime.SetWindowGroupAsync(shownWindow, pending.Id);
        await lifetime.CompleteCreationAsync(pendingReservation, shownWindow);
        Assert.Equal(0, await lifetime.DeleteEmptyAsync(ct));
        await context.Stash.AddAsync(pending.Id, [file], null, ct);
        Assert.Single(await context.Items.GetByGroupAsync(pending.Id, ct));
    }

    [Fact]
    public async Task UpgradeFromV5BacksUpDatabaseAndOnlyCollectsRecognizedEmptyTemporaryGroups()
    {
        using var context = new ApplyTestContext();
        var ct = TestContext.Current.CancellationToken;
        var source = context.Workspace.CreateFile("v5.txt", "legacy snapshot");
        var (hash, size) = await context.BlobStore.PutFileAsync(source, ct);
        await using (var connection = await context.Workspace.ConnectionFactory.OpenAsync(ct))
        {
            await connection.ExecuteAsync(new CommandDefinition("CREATE TABLE SchemaVersion (Version INTEGER NOT NULL);", cancellationToken: ct));
            foreach (var migration in Migrations.All.Where(migration => migration.Version <= 5))
            {
                await connection.ExecuteAsync(new CommandDefinition(migration.Sql, cancellationToken: ct));
                await connection.ExecuteAsync(new CommandDefinition("INSERT INTO SchemaVersion VALUES (@Version);", new { migration.Version }, cancellationToken: ct));
            }
            foreach (var (id, name) in new[] { ("default", "默认暂存"), ("empty", "暂存区 · 11-14 17:07:58"), ("keep", "暂存区 · 11-14 17:07:21") })
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO StashGroup (Id,Name,CreatedAt,UpdatedAt) VALUES (@Id,@Name,@Now,@Now);",
                    new { Id = id, Name = name, Now = DateTimeOffset.UtcNow }, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO StashItem (Id,GroupId,Kind,DisplayName,SourcePath,SourcePathKey,RootPath,RelativePath,FileCount,TotalSize,AddedAt,SnapshotAt)
                VALUES ('item','keep',0,'v5.txt',@Source,@Source,@Root,'v5.txt',1,@Size,@Now,@Now);
                INSERT INTO StashEntry (ItemId,RelativePath,IsDirectory,Sha256,Size) VALUES ('item','v5.txt',0,@Hash,@Size);
                """, new { Source = source, Root = context.Workspace.Root, Size = size, Hash = hash, Now = DateTimeOffset.UtcNow }, cancellationToken: ct));
        }
        Assert.Equal(6, await context.Migrator.MigrateAsync(ct));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(context.Paths.DatabasePath)!, Path.GetFileName(context.Paths.DatabasePath) + ".schema-v5-*.bak"));
        await ShelfGroupMigration.AdoptLegacyAsync(context.Groups, ct);
        Assert.Equal(1, await new ShelfLifetimeService(context.Groups, context.Stash).DeleteEmptyAsync(ct));
        Assert.Null(await context.Groups.GetAsync("empty", ct));
        Assert.Equal(StashGroupKind.Default, (await context.Groups.GetAsync("default", ct))!.Kind);
        Assert.Equal("item", Assert.Single(await context.Items.GetByGroupAsync("keep", ct)).Id);
        Assert.True(context.BlobStore.Exists(hash));
        Assert.Equal("legacy snapshot", File.ReadAllText(source));
    }

    [Fact]
    public async Task NonemptyTemporaryDefaultAndSavedGroupsNeverExpire()
    {
        using var context = new ApplyTestContext();
        var ct = TestContext.Current.CancellationToken;
        await context.Migrator.MigrateAsync(ct);
        var nonempty = await context.Stash.CreateGroupAsync("temporary", StashGroupKind.Temporary);
        var saved = await context.Stash.CreateGroupAsync("saved");
        var permanent = await context.Stash.CreateGroupAsync("default", StashGroupKind.Default);
        var source = context.Workspace.CreateFile("source.txt", "keep source and snapshot");
        await context.Stash.AddAsync(nonempty.Id, [source], null, ct);
        nonempty.ClosedAt = DateTimeOffset.UtcNow.AddYears(-10);
        await context.Groups.UpdateAsync(nonempty, ct);
        var lifetime = new ShelfLifetimeService(context.Groups, context.Stash);
        Assert.Equal(0, await lifetime.DeleteEmptyAsync(ct));
        Assert.NotNull(await context.Groups.GetAsync(saved.Id, ct));
        Assert.NotNull(await context.Groups.GetAsync(permanent.Id, ct));
        Assert.Single(await context.Items.GetByGroupAsync(nonempty.Id, ct));
        Assert.Equal("keep source and snapshot", File.ReadAllText(source));
    }

    [Fact]
    public async Task ReservationAndEveryOpenWindowProtectANewEmptyShelfUntilLastRelease()
    {
        using var context = new ApplyTestContext();
        var ct = TestContext.Current.CancellationToken;
        await context.Migrator.MigrateAsync(ct);
        var lifetime = new ShelfLifetimeService(context.Groups, context.Stash);
        var reservation = new object();
        var group = await lifetime.CreateTemporaryAsync(reservation, "new drop target");
        Assert.Equal(0, await lifetime.DeleteEmptyAsync(ct));
        var first = new object(); var second = new object();
        await lifetime.SetWindowGroupAsync(first, group.Id);
        await lifetime.SetWindowGroupAsync(second, group.Id);
        await lifetime.SetWindowGroupAsync(reservation, null);
        await lifetime.SetWindowGroupAsync(first, null);
        Assert.NotNull(await context.Groups.GetAsync(group.Id, ct));
        Assert.Equal(0, await lifetime.DeleteEmptyAsync(ct));
        await lifetime.SetWindowGroupAsync(second, null);
        Assert.Null(await context.Groups.GetAsync(group.Id, ct));
    }

    [Fact]
    public async Task RemovingLastItemCollectsOnlyTemporaryGroupAndNotAnEmptyFolderItem()
    {
        using var context = new ApplyTestContext();
        var ct = TestContext.Current.CancellationToken;
        await context.Migrator.MigrateAsync(ct);
        var temporary = await context.Stash.CreateGroupAsync("temporary", StashGroupKind.Temporary);
        var permanent = await context.Stash.CreateGroupAsync("default", StashGroupKind.Default);
        var folderGroup = await context.Stash.CreateGroupAsync("folder", StashGroupKind.Temporary);
        var source = context.Workspace.CreateFile("source.txt", "safe");
        var emptyFolder = Directory.CreateDirectory(Path.Combine(context.Workspace.Root, "empty-folder")).FullName;
        var temporaryItem = Assert.Single((await context.Stash.AddAsync(temporary.Id, [source], null, ct)).Added);
        var defaultItem = Assert.Single((await context.Stash.AddAsync(permanent.Id, [source], null, ct)).Added);
        await context.Stash.AddAsync(folderGroup.Id, [emptyFolder], null, ct);
        await context.Stash.RemoveItemsAsync([temporaryItem.Id, defaultItem.Id]);
        Assert.Null(await context.Groups.GetAsync(temporary.Id, ct));
        Assert.NotNull(await context.Groups.GetAsync(permanent.Id, ct));
        Assert.Equal(0, await new ShelfLifetimeService(context.Groups, context.Stash).DeleteEmptyAsync(ct));
        Assert.Single(await context.Items.GetByGroupAsync(folderGroup.Id, ct));
        Assert.True(Directory.Exists(emptyFolder));
        Assert.Equal("safe", File.ReadAllText(source));
    }

    [Fact]
    public async Task MovingLastItemCollectsSourceButPreservesDestinationSnapshot()
    {
        using var context = new ApplyTestContext();
        var ct = TestContext.Current.CancellationToken;
        await context.Migrator.MigrateAsync(ct);
        var sourceGroup = await context.Stash.CreateGroupAsync("source", StashGroupKind.Temporary);
        var target = await context.Stash.CreateGroupAsync("target", StashGroupKind.Temporary);
        var file = context.Workspace.CreateFile("move.txt", "keep");
        var item = Assert.Single((await context.Stash.AddAsync(sourceGroup.Id, [file], null, ct)).Added);
        await context.Stash.MoveItemsAsync([item.Id], target.Id);
        Assert.Null(await context.Groups.GetAsync(sourceGroup.Id, ct));
        var moved = Assert.Single(await context.Items.GetByGroupAsync(target.Id, ct));
        Assert.Equal(item.Id, moved.Id);
        var entry = Assert.Single(await context.Entries.GetByItemAsync(moved.Id, ct));
        Assert.True(context.BlobStore.Exists(entry.Sha256!));
        Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public async Task LegacyAdoptionRecognizesOnlyOldGeneratedNamesAndProtectsRenamedDefaults()
    {
        using var context = new ApplyTestContext();
        var ct = TestContext.Current.CancellationToken;
        await context.Migrator.MigrateAsync(ct);
        var oldDefault = await context.Stash.CreateGroupAsync("默认暂存", StashGroupKind.Legacy);
        var oldTemporary = await context.Stash.CreateGroupAsync("暂存区 · 11-14 17:07:58", StashGroupKind.Legacy);
        var named = await context.Stash.CreateGroupAsync("暂存区 · 11-14 17:07:58");
        var custom = await context.Stash.CreateGroupAsync("我的空箱", StashGroupKind.Legacy);
        await ShelfGroupMigration.AdoptLegacyAsync(context.Groups, ct);
        Assert.Equal(StashGroupKind.Default, (await context.Groups.GetAsync(oldDefault.Id, ct))!.Kind);
        await context.Stash.RenameGroupAsync(oldDefault.Id, "renamed default");
        Assert.Equal(1, await new ShelfLifetimeService(context.Groups, context.Stash).DeleteEmptyAsync(ct));
        Assert.Null(await context.Groups.GetAsync(oldTemporary.Id, ct));
        Assert.NotNull(await context.Groups.GetAsync(oldDefault.Id, ct));
        Assert.NotNull(await context.Groups.GetAsync(named.Id, ct));
        Assert.NotNull(await context.Groups.GetAsync(custom.Id, ct));
    }

    [Fact]
    public async Task CleanupRacingWithAddsNeverDeletesFilesOrLosesQueuedInput()
    {
        using var context = new ApplyTestContext();
        var ct = TestContext.Current.CancellationToken;
        await context.Migrator.MigrateAsync(ct);
        await context.Stash.CreateGroupAsync("default", StashGroupKind.Default);
        var lifetime = new ShelfLifetimeService(context.Groups, context.Stash);
        for (var index = 0; index < 8; index++)
        {
            var group = await context.Stash.CreateGroupAsync("race", StashGroupKind.Temporary);
            var source = context.Workspace.CreateFile($"race-{index}.txt", "preserve queued input");
            await Task.WhenAll(Task.Run(() => context.Stash.AddAsync(group.Id, [source], null, ct), ct),
                Task.Run(() => lifetime.DeleteEmptyAsync(ct), ct));
            Assert.True(File.Exists(source));
        }
        var all = await context.Groups.GetAllAsync(ct);
        var items = await Task.WhenAll(all.Select(group => context.Items.GetByGroupAsync(group.Id, ct)));
        Assert.Equal(8, items.Sum(group => group.Count));
    }
}
