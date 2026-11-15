
using System.Reflection;
using FileStash.Core.Services;
using FileStash.Core.Utils;
using FileStash.Infrastructure.Data;
using Microsoft.Extensions.Hosting;

namespace FileStash.Core.Tests;

public sealed class SeedIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSeedMustPreserveExistingLibrary(bool dedicatedDirectory)
    {
        using var temp = new TempWorkspace();
        var workspace = temp.Root;
        var home = dedicatedDirectory ? Path.Combine(workspace, ".stashbay-fixture") : workspace;
        var paths = new AppPaths(home, home);
        paths.EnsureDirectories();
        var factory = new SqliteConnectionFactory(paths);
        await new DatabaseMigrator(factory).MigrateAsync(TestContext.Current.CancellationToken);
        var groups = new StashGroupRepository(factory);
        var now = DateTimeOffset.UtcNow;
        await groups.AddAsync(new() { Id = "sentinel", Name = "sentinel", CreatedAt = now, UpdatedAt = now },
            TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(workspace, "inbox"));
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var appType = typeof(FileStash.App.App);
        using var host = (IHost)appType.GetMethod("BuildHost", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [paths, new SettingsService(paths)])!;
        var seedType = appType.Assembly.GetType("FileStash.App.Diagnostics.SeedRunner")!;
        var method = seedType.GetMethod("SeedAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await (Task<int>)method.Invoke(null, [host.Services, workspace])!);
        Assert.Equal("sentinel", (await groups.GetAllAsync(TestContext.Current.CancellationToken)).Single().Id);
    }

    [Fact]
    public async Task ValidSeedMustRepeatAndRejectForeignContentOrIncompleteFixture()
    {
        using var temp = new TempWorkspace();
        var workspace = temp.Root;
        var home = Path.Combine(workspace, ".stashbay-fixture");
        var paths = new AppPaths(home, home);
        paths.EnsureDirectories();
        foreach (var relative in new[]
        {
            "inbox/a.txt", "inbox/改过的.txt", "inbox/被删掉的.txt", "inbox/大文件.bin",
            "inbox/素材/字幕.srt", "inbox/素材/纹理.png", "inbox/素材/音效.wav",
            "inbox/设计稿-首页-2026年第三季度-最终版-张三李四王五复核.psd", "inbox/季度报表 (修订).xlsx",
            "repo/src/components/VeryLongComponentNameThatIsSureToBeTruncatedInTheNameColumn.tsx",
            "repo/src/utils/path-mapper.ts", "repo/README.md"
        }) temp.CreateFile(relative, relative);
        Directory.CreateDirectory(Path.Combine(workspace, "inbox/空目录"));
        using var host = (IHost)typeof(FileStash.App.App)
            .GetMethod("BuildHost", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [paths, new SettingsService(paths)])!;
        var method = typeof(FileStash.App.App).Assembly.GetType("FileStash.App.Diagnostics.SeedRunner")!
            .GetMethod("SeedAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        Task<int> Seed() => (Task<int>)method.Invoke(null, [host.Services, workspace])!;
        Assert.Equal(0, await Seed());
        Assert.Equal(0, await Seed());
        var factory = new SqliteConnectionFactory(paths);
        var groups = new StashGroupRepository(factory);
        var before = (await groups.GetAllAsync(TestContext.Current.CancellationToken)).Select(group => group.Id).Order().ToArray();
        var required = Path.Combine(workspace, "repo/README.md");
        File.Delete(required);
        await Assert.ThrowsAsync<FileNotFoundException>(Seed);
        Assert.Equal(before, (await groups.GetAllAsync(TestContext.Current.CancellationToken)).Select(group => group.Id).Order());
        File.WriteAllText(required, "restored");
        var now = DateTimeOffset.UtcNow;
        await groups.AddAsync(new() { Id = "foreign", Name = "foreign", CreatedAt = now, UpdatedAt = now },
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(Seed);
        Assert.Equal(5, (await groups.GetAllAsync(TestContext.Current.CancellationToken)).Count);
    }
}
