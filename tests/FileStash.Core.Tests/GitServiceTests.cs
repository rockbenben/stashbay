using FileStash.Core.Models;
using FileStash.Core.Services;
using FileStash.Infrastructure.Services;

namespace FileStash.Core.Tests;

public sealed class GitServiceTests : IAsyncLifetime
{
    private readonly ApplyTestContext _context = new();

    public async ValueTask InitializeAsync() => await _context.Migrator.MigrateAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        _context.Dispose();
        return ValueTask.CompletedTask;
    }

    private GitService Git => new();

    [Fact]
    public void ParsePorcelain_ShouldReadSimpleEntries()
    {
        // 真实字节：' ' 'M' ' ' 'a' ... → 路径从第 3 位开始
        var entries = GitService.ParsePorcelain(" M a.txt\0?? b.txt\0");

        Assert.Equal(2, entries.Count);
        Assert.Equal(" M", entries[0].Status);
        Assert.Equal("a.txt", entries[0].Path);
        Assert.Equal("??", entries[1].Status);
        Assert.Equal("b.txt", entries[1].Path);
    }

    [Fact]
    public void ParsePorcelain_ShouldReadRenameAsTwoFields()
    {
        // -z 模式下重命名占两个字段（新路径在前、原路径在后）。
        // 按"每行一字段"解析会把原路径当成下一个条目 —— porcelain 解析最常见的错。
        // 实测字节：'R' ' ' ' ' 'e' ... → 路径仍然从第 3 位开始
        var entries = GitService.ParsePorcelain("R  e.txt\0c.txt\0 M other.txt\0");

        Assert.Equal(2, entries.Count);
        Assert.Equal("e.txt", entries[0].Path);
        Assert.Equal("c.txt", entries[0].OriginalPath);
        Assert.Equal("other.txt", entries[1].Path);
    }

    [Fact]
    public void ParsePorcelain_ShouldMarkDeletedEntries()
    {
        var entries = GitService.ParsePorcelain("D  gone.txt\0");

        Assert.True(entries[0].IsDeleted);
    }

    [Fact]
    public async Task GetStatusAsync_ShouldReportChangesInRealRepository()
    {
        if (!GitHelper.IsAvailable)
        {
            return;
        }

        var repo = Path.Combine(_context.Workspace.Root, "repo");
        Directory.CreateDirectory(repo);
        GitHelper.InitRepository(repo);

        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "内容", TestContext.Current.CancellationToken);
        GitHelper.CommitAll(repo, "init");

        // 改一个、加一个
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "改了", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo, "b.txt"), "新的", TestContext.Current.CancellationToken);
        GitHelper.CommitAll(repo, "second");

        // 再改一次 a.txt（这次是已跟踪文件的修改），再加一个未跟踪的
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "又改了", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(repo, "new.txt"), "新增", TestContext.Current.CancellationToken);

        var status = await Git.GetStatusAsync(repo, TestContext.Current.CancellationToken);

        Assert.Contains(status, entry => entry.Path == "a.txt" && entry.IsModified);
        Assert.Contains(status, entry => entry.Path == "new.txt" && entry.IsUntracked);
    }

    [Fact]
    public async Task FilterIgnoredAsync_ShouldFindIgnoredPaths()
    {
        if (!GitHelper.IsAvailable)
        {
            return;
        }

        var repo = Path.Combine(_context.Workspace.Root, "repo");
        Directory.CreateDirectory(repo);
        GitHelper.InitRepository(repo);

        await File.WriteAllTextAsync(
            Path.Combine(repo, ".gitignore"),
            "ignored/\n*.log\n",
            TestContext.Current.CancellationToken);

        GitHelper.CommitAll(repo, "init");

        var ignored = await Git.FilterIgnoredAsync(
            repo,
            ["ignored/x.txt", "keep.txt", "a.log"],
            TestContext.Current.CancellationToken);

        Assert.Contains("ignored/x.txt", ignored);
        Assert.Contains("a.log", ignored);
        Assert.DoesNotContain("keep.txt", ignored);
    }

    [Fact]
    public async Task AddAsync_ShouldStageFiles()
    {
        if (!GitHelper.IsAvailable)
        {
            return;
        }

        var repo = Path.Combine(_context.Workspace.Root, "repo");
        Directory.CreateDirectory(repo);
        GitHelper.InitRepository(repo);

        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "内容", TestContext.Current.CancellationToken);

        await Git.AddAsync(repo, ["a.txt"], TestContext.Current.CancellationToken);

        var status = await Git.GetStatusAsync(repo, TestContext.Current.CancellationToken);

        Assert.Contains(status, entry => entry.Path == "a.txt" && entry.Status.StartsWith('A'));
    }

    [Fact]
    public async Task RespectGitIgnore_ShouldSkipIgnoredFilesWhenStashing()
    {
        if (!GitHelper.IsAvailable)
        {
            return;
        }

        var repo = Path.Combine(_context.Workspace.Root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "ignored"));
        GitHelper.InitRepository(repo);

        await File.WriteAllTextAsync(
            Path.Combine(repo, ".gitignore"),
            "ignored/\n",
            TestContext.Current.CancellationToken);

        GitHelper.CommitAll(repo, "init");

        await File.WriteAllTextAsync(
            Path.Combine(repo, "ignored", "x.txt"),
            "不该被暂存",
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(repo, "keep.txt"),
            "要暂存",
            TestContext.Current.CancellationToken);

        var settings = new SettingsService(_context.Paths);
        settings.Load();
        settings.Current.RespectGitIgnore = true;
        settings.Save(settings.Current);

        // 注意要用**同一个**设置实例：新建一个但不 Load 的话拿到的是默认值
        var stash = new StashService(
            _context.Groups,
            _context.Items,
            _context.Entries,
            _context.BlobStore,
            Git,
            settings);

        var groupId = await _context.CreateGroupAsync();
        var result = await stash.AddAsync(groupId, [repo], null, TestContext.Current.CancellationToken);

        var entries = await _context.Entries.GetByItemAsync(result.Added[0].Id, TestContext.Current.CancellationToken);

        // .gitignore 自己 + keep.txt（.git 目录整个跳过，ignored/ 被 gitignore 忽略）
        Assert.Equal(2, result.Added[0].FileCount);
        Assert.Contains("keep.txt", entries.Select(entry => entry.RelativePath));

        // 被忽略的文件不该在里面
        Assert.DoesNotContain(entries, entry => entry.RelativePath.Contains("ignored", StringComparison.OrdinalIgnoreCase));

        // .git 里的内部文件一个都不能进来（注意 ".gitignore" 里也含 ".git"，要看路径段）
        Assert.DoesNotContain(
            entries,
            entry => entry.RelativePath
                .Split('\\')
                .Contains(".git", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BranchWatcher_ShouldRaiseEventOnBranchSwitch()
    {
        if (!GitHelper.IsAvailable)
        {
            return;
        }

        var repo = Path.Combine(_context.Workspace.Root, "repo");
        Directory.CreateDirectory(repo);
        GitHelper.InitRepository(repo);

        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "内容", TestContext.Current.CancellationToken);
        GitHelper.CommitAll(repo, "init");

        var watcher = new GitBranchWatcher(Git);
        using var signal = new SemaphoreSlim(0);

        string? newBranch = null;
        watcher.BranchChanged += (_, args) =>
        {
            newBranch = args.NewBranch;
            signal.Release();
        };

        await watcher.WatchAsync(repo, TestContext.Current.CancellationToken);

        // 给文件监视器一点时间真正建立监听 —— 否则紧跟着的 checkout 可能赶在监听就绪之前
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // 切分支
        GitHelper.CheckoutNewBranch(repo, "feature-x");

        Assert.True(
            await signal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken),
            watcher.LastError?.ToString() ?? "没有收到分支切换事件");
        Assert.Equal("feature-x", newBranch);
    }

    [Fact]
    public async Task Planner_ShouldWarnAboutUncommittedChanges()
    {
        if (!GitHelper.IsAvailable)
        {
            return;
        }

        var repo = Path.Combine(_context.Workspace.Root, "repo");
        Directory.CreateDirectory(repo);
        GitHelper.InitRepository(repo);

        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "原始", TestContext.Current.CancellationToken);
        GitHelper.CommitAll(repo, "init");

        var groupId = await _context.CreateGroupAsync();
        var result = await _context.Stash.AddAsync(
            groupId,
            [Path.Combine(repo, "a.txt")],
            null,
            TestContext.Current.CancellationToken);

        // 目标文件有未提交改动
        await File.WriteAllTextAsync(
            Path.Combine(repo, "a.txt"),
            "改了但没提交",
            TestContext.Current.CancellationToken);

        var planner = new ApplyPlanner(_context.Entries, Git);

        var plan = await planner.BuildPlanAsync(
            result.Added,
            new TargetSpec(TargetMode.OriginalLocation, null, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            TestContext.Current.CancellationToken);

        var entry = Assert.Single(plan.Entries);

        Assert.Equal(PlannedState.Overwrite, entry.State);
        Assert.Equal("目标有未提交改动，覆盖前会备份", entry.Warning);
    }
}
