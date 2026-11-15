using System.IO;
using System.Text.Json;
using FileStash.Core.Utils;
using System.Windows;
using FileStash.App.Resources;
using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace FileStash.App.Diagnostics;

/// <summary>
/// <c>--seed &lt;工作区&gt;</c>：把工作区里现成的文件与目录做成一份取景用的暂存库 ——
/// 多个箱、各种源状态、各种写入结果的历史、根预设。
/// </summary>
/// <remarks>
/// 为什么在库里播真数据、而不是在 harness 里造假对象：
/// 截图与探针要看的是"真数据长什么样"（中文长名、MB 档大小、分支名、被占用的报错）。
/// 造出来的对象绕过写入路径，界面上那一列就成了零像素。
///
/// 只动重定向后的库（<c>STASHBAY_HOME</c> / <c>FILESTASH_HOME</c>）：这个开关是给自动化用的，
/// 顺手改到用户真实的暂存区就是事故。
/// </remarks>
internal static class SeedRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, string workspace)
    {
        App.InSmoke = true;

        if (Application.Current is { } app)
        {
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        try
        {
            return await SeedAsync(services, workspace);
        }
        catch (Exception ex)
        {
            // 自查模式不弹模态框（那会把进程永远吊住），错误落结果文件
            File.WriteAllText(App.SmokeReportPath, ex.ToString());
            return 1;
        }
    }

    private static async Task<int> SeedAsync(IServiceProvider services, string workspace)
    {
        var stash = services.GetRequiredService<IStashService>();
        var groups = services.GetRequiredService<IStashGroupRepository>();
        var items = services.GetRequiredService<IStashItemRepository>();
        var entries = services.GetRequiredService<IStashEntryRepository>();
        var operations = services.GetRequiredService<IOperationRepository>();
        var operationEntries = services.GetRequiredService<IOperationEntryRepository>();
        var presets = services.GetRequiredService<IRootPresetRepository>();
        var planner = services.GetRequiredService<IApplyPlanner>();
        var executor = services.GetRequiredService<IApplyExecutor>();

        workspace = Path.GetFullPath(workspace);
        var paths = services.GetRequiredService<AppPaths>();
        var fixtureHome = Path.Combine(workspace, ".stashbay-fixture");
        var expected = new AppPaths(fixtureHome, fixtureHome);
        if (!string.Equals(paths.RootDirectory, expected.RootDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(paths.SettingsPath, expected.SettingsPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Seed requires the dedicated workspace/.stashbay-fixture home.");
        for (var directory = new DirectoryInfo(paths.RootDirectory); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null) throw new IOException(directory.FullName);

        var ownershipPath = Path.Combine(paths.RootDirectory, "seed-ownership.json");
        var inventory = await ReadInventoryAsync(groups, items, operations, presets);
        var owned = File.Exists(ownershipPath)
            ? JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(ownershipPath))
            : [];
        if (owned is null || !inventory.SequenceEqual(owned))
            throw new InvalidOperationException("Seed home contains content outside the fixture inventory.");

        var inbox = Path.Combine(workspace, "inbox");
        var repoRoot = Path.Combine(workspace, "repo");
        var outDir = Path.Combine(workspace, "out");

        // 前置条件要**大声失败**：路径不对说明播的不是我以为的那一份数据，
        // 静默跳过会让后面所有截图与读数都拍在错的库上
        RequireDirectory(inbox);
        RequireDirectory(repoRoot);
        foreach (var relative in new[]
        {
            "inbox/a.txt",
            "inbox/改过的.txt",
            "inbox/被删掉的.txt",
            "inbox/大文件.bin",
            "inbox/素材/字幕.srt",
            "inbox/素材/纹理.png",
            "inbox/素材/音效.wav",
            "inbox/设计稿-首页-2026年第三季度-最终版-张三李四王五复核.psd",
            "inbox/季度报表 (修订).xlsx",
            "repo/src/components/VeryLongComponentNameThatIsSureToBeTruncatedInTheNameColumn.tsx",
            "repo/src/utils/path-mapper.ts",
            "repo/README.md",
        })
        {
            var file = Path.Combine(workspace, relative);
            if (!File.Exists(file)) throw new FileNotFoundException(file);
            using var readable = File.OpenRead(file);
        }
        RequireDirectory(Path.Combine(inbox, "空目录"));
        Directory.CreateDirectory(outDir);

        // 幂等：重跑先把上一次的播掉，否则箱越加越多，截图与稿子对不上号
        foreach (var group in await groups.GetAllAsync(CancellationToken.None))
        {
            await stash.DeleteGroupAsync(group.Id);
        }

        foreach (var operation in await operations.GetAllAsync(CancellationToken.None))
        {
            await operations.DeleteAsync(operation.Id, CancellationToken.None);
        }

        foreach (var preset in await presets.GetAllAsync(CancellationToken.None))
        {
            await presets.DeleteAsync(preset.Id, CancellationToken.None);
        }

        var mainBox = await stash.CreateGroupAsync(Strings.DefaultGroupName, StashGroupKind.Default);
        var projectBox = await stash.CreateGroupAsync("项目 A · 前端");
        var longNameBox = await stash.CreateGroupAsync("2026 年第三季度营销物料汇总与设计稿备份（归档勿动）");
        await stash.CreateGroupAsync("空箱（还没放东西）");

        await stash.AddAsync(
            mainBox.Id,
            [
                Path.Combine(inbox, "a.txt"),
                Path.Combine(inbox, "改过的.txt"),
                Path.Combine(inbox, "被删掉的.txt"),
                Path.Combine(inbox, "大文件.bin"),
                Path.Combine(inbox, "素材"),
                Path.Combine(inbox, "空目录"),
            ],
            null,
            CancellationToken.None);

        await stash.AddAsync(
            projectBox.Id,
            [
                Path.Combine(repoRoot, "src", "components", "VeryLongComponentNameThatIsSureToBeTruncatedInTheNameColumn.tsx"),
                Path.Combine(repoRoot, "src", "utils", "path-mapper.ts"),
                Path.Combine(repoRoot, "README.md"),
            ],
            null,
            CancellationToken.None);

        await stash.AddAsync(
            longNameBox.Id,
            [
                Path.Combine(inbox, "设计稿-首页-2026年第三季度-最终版-张三李四王五复核.psd"),
                Path.Combine(inbox, "季度报表 (修订).xlsx"),
            ],
            null,
            CancellationToken.None);

        await SeedPresetsAsync(presets, workspace);

        // 真跑一次写入：历史里"成功"那一条与它的每个条目都由产品自己写出来，
        // 不是我照着字段名编的
        var groupItems = await items.GetByGroupAsync(mainBox.Id, CancellationToken.None);
        var targets = await CollectTargetsAsync(items, entries, groupItems, outDir);

        if (targets.Count < 5)
        {
            throw new InvalidOperationException($"夹具写入目标太少（{targets.Count} 条），真跑一次写入做不出有代表性的历史");
        }

        var plan = await planner.BuildPlanAsync(
            groupItems,
            new TargetSpec(TargetMode.SpecifiedPath, outDir, StructureMode.FullRelative),
            ConflictPolicy.Overwrite,
            CancellationToken.None);

        await executor.ExecuteAsync(plan, null, CancellationToken.None);

        // 把落点改出三种模样，让预览里同时出现"新建 / 覆盖 / 相同 / 类型冲突"
        File.WriteAllText(targets[0], "目标后来被人改过了\r\n");

        // 上一轮这里留下的可能是目录，File.Delete 打在目录上抛 UnauthorizedAccessException，
        // 夹具会半途报废（箱和历史此刻已写进库）—— 按实际类型清掉再重建。
        if (Directory.Exists(targets[1]))
        {
            Directory.Delete(targets[1], true);
        }
        else
        {
            File.Delete(targets[1]);
        }

        Directory.CreateDirectory(targets[1]);

        await SeedExtraOperationsAsync(operations, operationEntries, targets, outDir, mainBox.Id);

        await File.WriteAllTextAsync(ownershipPath,
            JsonSerializer.Serialize(await ReadInventoryAsync(groups, items, operations, presets)));
        Fixture.Save(workspace, mainBox.Id);

        var groupCount = (await groups.GetAllAsync(CancellationToken.None)).Count;

        File.WriteAllText(
            App.SmokeReportPath,
            $"OK groups={groupCount} mainBoxItems={groupItems.Count} targets={targets.Count}");

        return 0;
    }

    private static async Task<string[]> ReadInventoryAsync(IStashGroupRepository groups,
        IStashItemRepository items, IOperationRepository operations, IRootPresetRepository presets)
    {
        var ids = new List<string>();
        foreach (var group in await groups.GetAllAsync(CancellationToken.None))
        {
            ids.Add("group/" + group.Id);
            ids.AddRange((await items.GetByGroupAsync(group.Id, CancellationToken.None))
                .Select(item => "item/" + item.Id));
        }
        ids.AddRange((await operations.GetAllAsync(CancellationToken.None)).Select(operation => "operation/" + operation.Id));
        ids.AddRange((await presets.GetAllAsync(CancellationToken.None)).Select(preset => "preset/" + preset.Id));
        return ids.Order(StringComparer.Ordinal).ToArray();
    }

    private static void RequireDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"夹具工作区缺目录：{path}");
        }
    }

    private static async Task<List<string>> CollectTargetsAsync(
        IStashItemRepository items,
        IStashEntryRepository entries,
        IReadOnlyList<StashItem> groupItems,
        string outDir)
    {
        var targets = new List<string>();

        foreach (var item in groupItems)
        {
            foreach (var entry in await entries.GetByItemAsync(item.Id, CancellationToken.None))
            {
                if (entry.IsDirectory)
                {
                    continue;
                }

                targets.Add(Path.Combine(outDir, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            }
        }

        return targets;
    }

    private static async Task SeedPresetsAsync(IRootPresetRepository presets, string workspace)
    {
        await presets.AddAsync(
            new RootPreset
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "下载目录",
                Path = Path.Combine(workspace, "out", "下载"),
                SortOrder = 0,
            },
            CancellationToken.None);

        await presets.AddAsync(
            new RootPreset
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "共享盘 · 设计物料归档（很长的预设名字看看怎么截）",
                Path = @"\\nas-team\设计物料\2026\第三季度\归档",
                SortOrder = 1,
            },
            CancellationToken.None);

        await presets.AddAsync(
            new RootPreset
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "D",
                Path = @"D:\",
                SortOrder = 2,
            },
            CancellationToken.None);
    }

    /// <summary>
    /// 补三条真跑不出来的历史：部分失败（占用 / 无权限 / 原样冒出来的 .NET 消息）、
    /// 中止、已撤销。写盘失败的分支要靠活的占用者，夹具里造不出来，只能照产品自己的措辞播。
    /// </summary>
    private static async Task SeedExtraOperationsAsync(
        IOperationRepository operations,
        IOperationEntryRepository operationEntries,
        IReadOnlyList<string> targets,
        string outDir,
        string groupId)
    {
        var now = DateTimeOffset.Now;

        await WriteAsync(
            operations,
            operationEntries,
            new Operation
            {
                Id = Guid.NewGuid().ToString("N"),
                StartedAt = now.AddMinutes(-40),
                FinishedAt = now.AddMinutes(-40).AddSeconds(11),
                GroupId = groupId,
                TargetMode = TargetMode.SpecifiedPath,
                TargetDescription = outDir,
                Status = OperationStatus.PartiallyFailed,
            },
            [
                (targets[2], EntryResult.Overwritten, null),
                (targets[3], EntryResult.Failed, Core.Resources.Strings.Format(
                    Core.Resources.Strings.LockedByProcess,
                    "EXCEL.EXE")),
                (targets[4], EntryResult.Failed, Core.Resources.Strings.NoPermission),
                (targets[5], EntryResult.Failed,
                    @"The process cannot access the file 'D:\Temp\季度报表 (修订).xlsx' because it is being used by another process."),
            ]);

        await WriteAsync(
            operations,
            operationEntries,
            new Operation
            {
                Id = Guid.NewGuid().ToString("N"),
                StartedAt = now.AddHours(-3),
                FinishedAt = now.AddHours(-3).AddSeconds(2),
                GroupId = groupId,
                TargetMode = TargetMode.AnotherRoot,
                TargetDescription = @"D:\proj-b",
                Status = OperationStatus.Cancelled,
            },
            [
                (targets[0], EntryResult.Skipped, Core.Resources.Strings.Cancelled),
            ]);

        await WriteAsync(
            operations,
            operationEntries,
            new Operation
            {
                Id = Guid.NewGuid().ToString("N"),
                StartedAt = now.AddDays(-1),
                FinishedAt = now.AddDays(-1).AddSeconds(5),
                GroupId = groupId,
                TargetMode = TargetMode.OriginalLocation,
                TargetDescription = Core.Resources.Strings.TargetOriginalLocation,
                Status = OperationStatus.Undone,
            },
            [
                (targets[1], EntryResult.Overwritten, null),
                (targets[2], EntryResult.Created, null),
            ]);

        await WriteAsync(
            operations,
            operationEntries,
            new Operation
            {
                Id = Guid.NewGuid().ToString("N"),
                StartedAt = now.AddDays(-2),
                FinishedAt = now.AddDays(-2).AddSeconds(1),
                GroupId = groupId,
                TargetMode = TargetMode.SpecifiedPath,
                TargetDescription = @"C:\Windows\System32\config\filestash-target.bin",
                Status = OperationStatus.Failed,
            },
            [
                (targets[3], EntryResult.Failed, Core.Resources.Strings.NoPermission),
            ]);
    }

    private static async Task WriteAsync(
        IOperationRepository operations,
        IOperationEntryRepository operationEntries,
        Operation operation,
        IReadOnlyList<(string TargetPath, EntryResult Action, string? Error)> rows)
    {
        var created = rows.Count(row => row.Action == EntryResult.Created);
        var overwritten = rows.Count(row => row.Action is EntryResult.Overwritten or EntryResult.Renamed);
        var skipped = rows.Count(row => row.Action == EntryResult.Skipped);
        var failed = rows.Count(row => row.Action == EntryResult.Failed);

        operation.Summary = $"{{\"created\":{created},\"overwritten\":{overwritten},\"skipped\":{skipped},\"failed\":{failed}}}";

        await operations.AddAsync(operation, CancellationToken.None);

        await operationEntries.AddManyAsync(
            rows.Select(row => new OperationEntry
            {
                OperationId = operation.Id,
                TargetPath = row.TargetPath,
                Action = row.Action,
                NewSha256 = new string('a', 64),
                BackupSha256 = row.Action == EntryResult.Overwritten ? new string('b', 64) : null,
                Error = row.Error,
            }).ToList(),
            CancellationToken.None);
    }
}
