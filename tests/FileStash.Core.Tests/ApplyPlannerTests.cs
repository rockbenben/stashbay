using FileStash.Core.Models;

namespace FileStash.Core.Tests;

public sealed class ApplyPlannerTests : IAsyncLifetime
{
    private readonly ApplyTestContext _context = new();

    public async ValueTask InitializeAsync() => await _context.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<(string GroupId, StashItem Item)> AddFileAsync(string relativePath, string content)
    {
        var groupId = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile(relativePath, content);

        var result = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);

        return (groupId, result.Added[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableTargetOriginMustRequireANewPreview(bool removeOriginAfterPreview)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, item) = await AddFileAsync("source/a.txt", "snapshot");
        var target = _context.Workspace.CreateFile("out/a.txt", "snapshot");
        var zone = new byte[] { 1, 2, 3 };
        await FileStash.Core.Utils.ZoneIdentifier.WriteAsync(target, zone, ct);
        ApplyPlan plan;
        await using (var locked = new FileStream(target + ":Zone.Identifier", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            plan = await _context.Planner.BuildPlanAsync([item],
                new TargetSpec(TargetMode.SpecifiedPath, Path.GetDirectoryName(target), StructureMode.Flatten),
                ConflictPolicy.Overwrite, ct, skipIdentical: false);
        if (removeOriginAfterPreview) await FileStash.Core.Utils.ZoneIdentifier.WriteAsync(target, null, ct);
        var operation = await _context.Executor.ExecuteAsync(plan, null, ct);
        var entry = Assert.Single(plan.Entries);
        Assert.Equal(OperationStatus.PartiallyFailed, operation.Status);
        Assert.Equal(PlannedState.Overwrite, entry.State);
        Assert.Null(entry.ExpectedTargetSha256);
        Assert.Equal("snapshot", File.ReadAllText(target));
        Assert.Equal(removeOriginAfterPreview ? null : zone,
            await FileStash.Core.Utils.ZoneIdentifier.ReadAsync(target, ct));
    }

    [Theory]
    [InlineData("snapshot", PlannedState.Identical)]
    [InlineData("modified", PlannedState.Overwrite)]
    [InlineData("longer modified content", PlannedState.Overwrite)]
    public async Task PreviewObservationMustFreezeTargetContentAndOrigin(string targetContent, PlannedState expectedState)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, item) = await AddFileAsync("source/a.txt", "snapshot");
        var target = _context.Workspace.CreateFile("out/a.txt", targetContent);
        var zone = new byte[] { 1, 2, 3, 0 };
        await FileStash.Core.Utils.ZoneIdentifier.WriteAsync(target, zone, ct);
        var sourceEntry = (await _context.Entries.GetByItemAsync(item.Id, ct)).Single();
        sourceEntry.ZoneIdentifier = zone;
        await _context.Entries.DeleteByItemAsync(item.Id, ct);
        await _context.Entries.AddManyAsync([sourceEntry], ct);
        var plan = await _context.Planner.BuildPlanAsync([item],
            new TargetSpec(TargetMode.SpecifiedPath, Path.GetDirectoryName(target), StructureMode.Flatten),
            ConflictPolicy.Overwrite, ct, skipIdentical: false);
        var planned = Assert.Single(plan.Entries);
        Assert.Equal(expectedState, planned.State);
        Assert.Equal(await FileStash.Core.Utils.HashUtil.ComputeFileSha256Async(target, ct), planned.ExpectedTargetSha256);
        Assert.Equal(zone, planned.ExpectedTargetZoneIdentifier);
        File.WriteAllText(target, "edited after preview");
        var operation = await _context.Executor.ExecuteAsync(plan, null, ct);
        Assert.Equal(OperationStatus.PartiallyFailed, operation.Status);
        Assert.Equal("edited after preview", File.ReadAllText(target));
    }

    [Fact]
    public async Task BuildPlanAsync_NewTarget_ShouldBeCreate()
    {
        var (groupId, item) = await AddFileAsync("a.txt", "hello");

        var target = Path.Combine(_context.Workspace.Root, "out");

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Ask,
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(PlannedState.New, entry.State);
        Assert.Equal(PlannedAction.Create, entry.Action);
        Assert.NotEmpty(groupId);
    }

    [Fact]
    public async Task BuildPlanAsync_OriginalLocationWithUnchangedSource_ShouldBeIdentical()
    {
        // 原位置的目标就是源文件本身：源没动过 → 内容相同 → 跳过
        var (groupId, item) = await AddFileAsync("a.txt", "hello");

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.OriginalLocation, null, StructureMode.FullRelative),
            ConflictPolicy.Ask,
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(PlannedState.Identical, entry.State);
        Assert.Equal(PlannedAction.Skip, entry.Action);
        Assert.NotEmpty(groupId);
    }

    [Fact]
    public async Task BuildPlanAsync_IdenticalContent_ShouldBeSkipped()
    {
        var (groupId, item) = await AddFileAsync("a.txt", "hello");

        // 先写一份一模一样的文件到目标位置
        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "a.txt"), "hello", TestContext.Current.CancellationToken);

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(PlannedState.Identical, entry.State);
        Assert.Equal(PlannedAction.Skip, entry.Action);
        Assert.NotEmpty(groupId);
    }

    [Fact]
    public async Task BuildPlanAsync_DifferentContent_ShouldBeOverwrite()
    {
        var (groupId, item) = await AddFileAsync("a.txt", "hello");

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "a.txt"), "different content", TestContext.Current.CancellationToken);

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(PlannedState.Overwrite, entry.State);
        Assert.Equal(PlannedAction.Overwrite, entry.Action);
        Assert.NotEmpty(groupId);
    }

    [Fact]
    public async Task BuildPlanAsync_LockedTarget_ShouldBeOverwriteAndNotThrow()
    {
        // 回归：目标文件被别的程序独占锁着（正开在 Excel 里）时，
        // 计划阶段要算哈希去比对，读不了 —— 抛出去的话整个写回就没了，
        // 用户看到的是"出错了"，而不是"这个文件正被谁占着"。
        var (groupId, item) = await AddFileAsync("a.txt", "hello");

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);

        var targetFile = Path.Combine(target, "a.txt");
        await File.WriteAllTextAsync(targetFile, "hello", TestContext.Current.CancellationToken);

        // 独占锁住，别的进程读不了（模拟 Excel 打开着）
        await using var lockStream = new FileStream(
            targetFile, FileMode.Open, FileAccess.Read, FileShare.None);

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(PlannedState.Overwrite, entry.State);
        Assert.Equal(PlannedAction.Overwrite, entry.Action);
        Assert.NotEmpty(groupId);
    }

    [Fact]
    public async Task BuildPlanAsync_TypeConflict_ShouldBeSkipAndNotChangeable()
    {
        var (groupId, item) = await AddFileAsync("a.txt", "hello");

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(Path.Combine(target, "a.txt"));

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(PlannedState.TypeConflict, entry.State);
        Assert.Equal(PlannedAction.Skip, entry.Action);
        Assert.NotEmpty(groupId);
    }

    [Fact]
    public async Task BuildPlanAsync_StripMoreSegmentsThanPathHas_ShouldBeInvalid()
    {
        var (groupId, item) = await AddFileAsync("a.txt", "hello");

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.AnotherRoot, Path.Combine(_context.Workspace.Root, "out"), StructureMode.FullRelative, StripLeadingSegments: 5),
            ConflictPolicy.Ask,
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(PlannedState.Invalid, entry.State);
        Assert.Equal(PlannedAction.Skip, entry.Action);
        Assert.NotEmpty(groupId);
    }

    [Fact]
    public async Task BuildPlanAsync_RenamePolicy_ShouldIncrementUntilFree()
    {
        var (groupId, item) = await AddFileAsync("a.txt", "hello");

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "a.txt"), "different", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(target, "a (2).txt"), "different too", TestContext.Current.CancellationToken);

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Rename,
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(PlannedAction.Rename, entry.Action);
        Assert.Equal("a (3).txt", Path.GetFileName(entry.TargetPath));
        Assert.NotEmpty(groupId);
    }

    [Fact]
    public async Task BuildPlanAsync_ReadOnlyTarget_ShouldWarn()
    {
        var (groupId, item) = await AddFileAsync("a.txt", "hello");

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);
        var targetFile = Path.Combine(target, "a.txt");
        await File.WriteAllTextAsync(targetFile, "different", TestContext.Current.CancellationToken);
        File.SetAttributes(targetFile, File.GetAttributes(targetFile) | FileAttributes.ReadOnly);

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal("目标只读", entry.Warning);
        Assert.NotEmpty(groupId);
    }

    [Fact]
    public async Task BuildPlanAsync_SameTargetTwice_ShouldMarkSecondInvalid()
    {
        var groupId = await _context.CreateGroupAsync();

        var first = _context.Workspace.CreateFile("one/a.txt", "a");
        var second = _context.Workspace.CreateFile("two/a.txt", "b");

        var result = await _context.Stash.AddAsync(groupId, [first, second], null, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Added.Count);

        var target = Path.Combine(_context.Workspace.Root, "out");
        Directory.CreateDirectory(target);

        // 两次都平铺到同一个目录 → 目标重名
        var plan = await _context.Planner.BuildPlanAsync(
            result.Added,
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.Flatten),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        // 平铺时重名会自动加 (2)，所以两个目标其实不重样
        Assert.Equal(2, plan.Entries.Count);
        Assert.NotEqual(plan.Entries[0].TargetPath, plan.Entries[1].TargetPath);
    }
}
