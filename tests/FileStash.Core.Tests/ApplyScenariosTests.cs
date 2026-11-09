using FileStash.Core.Models;

namespace FileStash.Core.Tests;

/// <summary>
/// 核心场景集成测试（切分支原位置写回与撤销、换根与剥离、结构模式与重名冲突）。
/// </summary>
public sealed class ApplyScenariosTests : IAsyncLifetime
{
    private readonly ApplyTestContext _context = new();

    public async ValueTask InitializeAsync() => await _context.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task S1_SwitchBranchThenApplyToOriginalLocation_ShouldRestoreStashedContent()
    {
        if (!GitHelper.IsAvailable)
        {
            return;
        }

        var repo = Path.Combine(_context.Workspace.Root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "folder"));

        GitHelper.InitRepository(repo);

        // 明确建出 branch-a（git init 出来的初始分支名不固定）
        GitHelper.CheckoutNewBranch(repo, "branch-a");

        // branch-a 的内容
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "branch-a 的内容", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo, "folder", "b.txt"), "branch-a 的文件夹内容", TestContext.Current.CancellationToken);
        GitHelper.CommitAll(repo, "branch-a");

        GitHelper.CheckoutNewBranch(repo, "branch-b");
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "branch-b 的内容", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo, "folder", "b.txt"), "branch-b 的文件夹内容", TestContext.Current.CancellationToken);
        GitHelper.CommitAll(repo, "branch-b");

        GitHelper.Checkout(repo, "branch-a");

        // 在 branch-a 上暂存 2 个文件 + 1 个文件夹
        var groupId = await _context.CreateGroupAsync();

        var addResult = await _context.Stash.AddAsync(
            groupId,
            [Path.Combine(repo, "a.txt"), Path.Combine(repo, "folder")],
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, addResult.Added.Count);

        // 仓库内的项：根自动是仓库根
        Assert.Equal(repo, addResult.Added[0].RootPath);
        Assert.Equal("branch-a", addResult.Added[0].GitBranch);

        // 切到 branch-b
        GitHelper.Checkout(repo, "branch-b");
        Assert.Equal("branch-b 的内容", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt"), TestContext.Current.CancellationToken));

        // 应用到原位置
        var plan = await _context.Planner.BuildPlanAsync(
            addResult.Added,
            new TargetSpec(TargetMode.OriginalLocation, null, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var operation = await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.Equal("branch-a 的内容", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("branch-a 的文件夹内容", await File.ReadAllTextAsync(Path.Combine(repo, "folder", "b.txt"), TestContext.Current.CancellationToken));

        // 撤销 → 恢复成 branch-b 的内容
        var undo = await _context.Executor.UndoAsync(operation.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, undo.Restored);
        Assert.Equal("branch-b 的内容", await File.ReadAllTextAsync(Path.Combine(repo, "a.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("branch-b 的文件夹内容", await File.ReadAllTextAsync(Path.Combine(repo, "folder", "b.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task S2_WriteToAnotherRoot_ShouldKeepSubPath()
    {
        // 根 A\ 下 x\y\z.txt
        var rootA = Path.Combine(_context.Workspace.Root, "A");
        var source = _context.Workspace.CreateFile(Path.Combine("A", "x", "y", "z.txt"), "内容");

        var groupId = await _context.CreateGroupAsync();
        var addResult = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);
        var item = addResult.Added[0];

        // 默认根是文件自己的父目录
        Assert.Equal(Path.Combine(rootA, "x", "y"), item.RootPath);

        // 把根设成 A\ → 子路径变成 x\y\z.txt
        await _context.Stash.SetRootAsync([item.Id], rootA);

        var updated = (await _context.Items.GetByIdsAsync([item.Id], TestContext.Current.CancellationToken)).Single();
        Assert.Equal(rootA, updated.RootPath);
        Assert.Equal(@"x\y\z.txt", updated.RelativePath);

        // 写入根 B\ → B\x\y\z.txt
        var rootB = Path.Combine(_context.Workspace.Root, "B");

        var plan = await _context.Planner.BuildPlanAsync(
            [updated],
            new TargetSpec(TargetMode.AnotherRoot, rootB, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(rootB, "x", "y", "z.txt")));
        Assert.Equal("内容", await File.ReadAllTextAsync(Path.Combine(rootB, "x", "y", "z.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task S2_StripLeadingSegment_ShouldRemoveOneLevel()
    {
        var rootA = Path.Combine(_context.Workspace.Root, "A");
        var source = _context.Workspace.CreateFile(Path.Combine("A", "x", "y", "z.txt"), "内容");

        var groupId = await _context.CreateGroupAsync();
        var addResult = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);
        var item = addResult.Added[0];

        await _context.Stash.SetRootAsync([item.Id], rootA);
        var updated = (await _context.Items.GetByIdsAsync([item.Id], TestContext.Current.CancellationToken)).Single();

        // 剥离 1 段 → B\y\z.txt
        var rootB = Path.Combine(_context.Workspace.Root, "B");

        var plan = await _context.Planner.BuildPlanAsync(
            [updated],
            new TargetSpec(TargetMode.AnotherRoot, rootB, StructureMode.FullRelative, StripLeadingSegments: 1),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(rootB, "y", "z.txt")));
        Assert.False(File.Exists(Path.Combine(rootB, "x")));
    }

    [Fact]
    public async Task S3_ThreeStructureModes_ShouldMapCorrectly()
    {
        var groupId = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile(Path.Combine("tree", "sub", "a.txt"), "内容");

        var addResult = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);
        var item = addResult.Added[0];

        // 根 = tree 的父目录 → 子路径 tree\sub\a.txt
        var root = Path.Combine(_context.Workspace.Root, "tree", "sub");
        await _context.Stash.SetRootAsync([item.Id], root);

        var updated = (await _context.Items.GetByIdsAsync([item.Id], TestContext.Current.CancellationToken)).Single();
        Assert.Equal("a.txt", updated.RelativePath);

        var target = Path.Combine(_context.Workspace.Root, "out");

        // FullRelative：out\a.txt
        var fullPlan = await _context.Planner.BuildPlanAsync(
            [updated],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(target, "a.txt"), Assert.Single(fullPlan.Entries).TargetPath);

        // Flatten：文件名直接放目标目录
        var flatPlan = await _context.Planner.BuildPlanAsync(
            [updated],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.Flatten),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(target, "a.txt"), Assert.Single(flatPlan.Entries).TargetPath);

        // ItemOnly：文件项就是文件名
        var itemOnlyPlan = await _context.Planner.BuildPlanAsync(
            [updated],
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.ItemOnly),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(target, "a.txt"), Assert.Single(itemOnlyPlan.Entries).TargetPath);
    }

    [Fact]
    public async Task S3_TreatTargetAsFileName_ShouldWriteExactlyThatName()
    {
        var groupId = await _context.CreateGroupAsync();
        var source = _context.Workspace.CreateFile(Path.Combine("tree", "a.txt"), "内容");

        var addResult = await _context.Stash.AddAsync(groupId, [source], null, TestContext.Current.CancellationToken);
        var item = addResult.Added[0];

        var target = Path.Combine(_context.Workspace.Root, "out");
        var targetFile = Path.Combine(target, "重命名.txt");

        var plan = await _context.Planner.BuildPlanAsync(
            [item],
            new TargetSpec(TargetMode.SpecifiedPath, targetFile, StructureMode.FullRelative, TreatTargetAsFileName: true),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        Assert.Equal(targetFile, Assert.Single(plan.Entries).TargetPath);

        await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(targetFile));
        Assert.Equal("内容", await File.ReadAllTextAsync(targetFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task S3_FlattenWithDuplicateNames_ShouldAddSuffix()
    {
        var groupId = await _context.CreateGroupAsync();
        var first = _context.Workspace.CreateFile(Path.Combine("one", "a.txt"), "1");
        var second = _context.Workspace.CreateFile(Path.Combine("two", "a.txt"), "2");

        var addResult = await _context.Stash.AddAsync(groupId, [first, second], null, TestContext.Current.CancellationToken);

        var target = Path.Combine(_context.Workspace.Root, "out");

        var plan = await _context.Planner.BuildPlanAsync(
            addResult.Added,
            new TargetSpec(TargetMode.SpecifiedPath, target, StructureMode.Flatten),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal(Path.Combine(target, "a.txt"), plan.Entries[0].TargetPath);
        Assert.Equal(Path.Combine(target, "a (2).txt"), plan.Entries[1].TargetPath);

        await _context.Executor.ExecuteAsync(plan, null, TestContext.Current.CancellationToken);

        Assert.Equal("1", await File.ReadAllTextAsync(Path.Combine(target, "a.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("2", await File.ReadAllTextAsync(Path.Combine(target, "a (2).txt"), TestContext.Current.CancellationToken));
    }
}
