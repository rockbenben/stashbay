using FileStash.Core.Models;
using FileStash.Core.Services;

namespace FileStash.Core.Tests;

public sealed class PathMapperTests
{
    private static readonly PathMapper Mapper = new();

    private static StashItem NewItem(string root = @"C:\repo", string relative = @"src\assets")
    {
        return new StashItem
        {
            Id = "item",
            GroupId = "group",
            Kind = StashItemKind.Folder,
            DisplayName = "assets",
            SourcePath = @"C:\repo\src\assets",
            SourcePathKey = @"C:\REPO\SRC\ASSETS",
            RootPath = root,
            RelativePath = relative,
        };
    }

    private static StashEntry NewEntry(string relative) => new()
    {
        ItemId = "item",
        RelativePath = relative,
        Sha256 = new string('a', 64),
        Size = 10,
    };

    [Fact]
    public void OriginalLocation_ShouldUseItemRootPath()
    {
        var item = NewItem();
        var entry = NewEntry(@"src\assets\logo.png");

        var path = Mapper.Map(
            item,
            entry,
            new TargetSpec(TargetMode.OriginalLocation, null, StructureMode.FullRelative),
            new PathMapper.MappingContext());

        Assert.Equal(@"C:\repo\src\assets\logo.png", path);
    }

    [Fact]
    public void OriginalLocation_ShouldIgnoreGitRepoRootAndUseItemRoot()
    {
        // GitRepoRoot 只是元数据，不能覆盖用户手动设过的根
        var item = NewItem(root: @"C:\custom-root");
        item.GitRepoRoot = @"C:\repo";

        var path = Mapper.Map(
            item,
            NewEntry(@"src\assets\logo.png"),
            new TargetSpec(TargetMode.OriginalLocation, null, StructureMode.FullRelative),
            new PathMapper.MappingContext());

        Assert.StartsWith(@"C:\custom-root", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnotherRoot_ShouldKeepFullRelativePath()
    {
        var path = Mapper.Map(
            NewItem(),
            NewEntry(@"src\assets\logo.png"),
            new TargetSpec(TargetMode.AnotherRoot, @"D:\target", StructureMode.FullRelative),
            new PathMapper.MappingContext());

        Assert.Equal(@"D:\target\src\assets\logo.png", path);
    }

    [Fact]
    public void AnotherRoot_ShouldStripLeadingSegments()
    {
        var path = Mapper.Map(
            NewItem(),
            NewEntry(@"src\assets\logo.png"),
            new TargetSpec(TargetMode.AnotherRoot, @"D:\target", StructureMode.FullRelative, StripLeadingSegments: 1),
            new PathMapper.MappingContext());

        Assert.Equal(@"D:\target\assets\logo.png", path);
    }

    [Fact]
    public void SpecifiedPath_FullRelative_ShouldKeepRelativePath()
    {
        var path = Mapper.Map(
            NewItem(),
            NewEntry(@"src\assets\logo.png"),
            new TargetSpec(TargetMode.SpecifiedPath, @"E:\out", StructureMode.FullRelative),
            new PathMapper.MappingContext());

        Assert.Equal(@"E:\out\src\assets\logo.png", path);
    }

    [Fact]
    public void SpecifiedPath_ItemOnly_ShouldKeepItemNameAndInnerStructure()
    {
        var path = Mapper.Map(
            NewItem(),
            NewEntry(@"src\assets\logo.png"),
            new TargetSpec(TargetMode.SpecifiedPath, @"E:\out", StructureMode.ItemOnly),
            new PathMapper.MappingContext());

        Assert.Equal(@"E:\out\assets\logo.png", path);
    }

    [Fact]
    public void SpecifiedPath_ItemOnly_ForFileItem_ShouldKeepFileNameOnly()
    {
        var item = NewItem(relative: @"src\login.ts");
        item.Kind = StashItemKind.File;
        item.DisplayName = "login.ts";

        var path = Mapper.Map(
            item,
            NewEntry(@"src\login.ts"),
            new TargetSpec(TargetMode.SpecifiedPath, @"E:\out", StructureMode.ItemOnly),
            new PathMapper.MappingContext());

        Assert.Equal(@"E:\out\login.ts", path);
    }

    [Fact]
    public void SpecifiedPath_Flatten_ShouldUseFileNameOnly()
    {
        var path = Mapper.Map(
            NewItem(),
            NewEntry(@"src\assets\logo.png"),
            new TargetSpec(TargetMode.SpecifiedPath, @"E:\out", StructureMode.Flatten),
            new PathMapper.MappingContext());

        Assert.Equal(@"E:\out\logo.png", path);
    }

    [Fact]
    public void SpecifiedPath_Flatten_ShouldRenameDuplicates()
    {
        var context = new PathMapper.MappingContext();
        var target = new TargetSpec(TargetMode.SpecifiedPath, @"E:\out", StructureMode.Flatten);

        var first = Mapper.Map(NewItem(), NewEntry(@"a\logo.png"), target, context);
        var second = Mapper.Map(NewItem(), NewEntry(@"b\logo.png"), target, context);
        var third = Mapper.Map(NewItem(), NewEntry(@"c\logo.png"), target, context);

        Assert.Equal(@"E:\out\logo.png", first);
        Assert.Equal(@"E:\out\logo (2).png", second);
        Assert.Equal(@"E:\out\logo (3).png", third);
    }

    [Fact]
    public void SpecifiedPath_TreatTargetAsFileName_ShouldUseTargetDirectly()
    {
        var path = Mapper.Map(
            NewItem(),
            NewEntry(@"src\assets\logo.png"),
            new TargetSpec(TargetMode.SpecifiedPath, @"E:\out\renamed.png", StructureMode.FullRelative, TreatTargetAsFileName: true),
            new PathMapper.MappingContext { SingleFile = true });

        Assert.Equal(@"E:\out\renamed.png", path);
    }

    [Fact]
    public void SpecifiedPath_TreatTargetAsFileName_ShouldBeIgnoredWhenMoreThanOneFile()
    {
        // 多文件时不许"目标即文件名"，映射器退回把它当目录（计划器会把这些条目标成 Invalid）
        var path = Mapper.Map(
            NewItem(),
            NewEntry(@"src\assets\logo.png"),
            new TargetSpec(TargetMode.SpecifiedPath, @"E:\out", StructureMode.FullRelative, TreatTargetAsFileName: true),
            new PathMapper.MappingContext { SingleFile = false });

        Assert.Equal(@"E:\out\src\assets\logo.png", path);
    }

    [Fact]
    public void DragExport_ShouldUseExportRootWithItemOnlyRules()
    {
        var path = Mapper.Map(
            NewItem(),
            NewEntry(@"src\assets\logo.png"),
            new TargetSpec(TargetMode.DragExport, null, StructureMode.ItemOnly),
            new PathMapper.MappingContext(),
            @"C:\export\abc");

        Assert.Equal(@"C:\export\abc\assets\logo.png", path);
    }

    [Fact]
    public void ExpandTargetPath_ShouldExpandEnvironmentVariablesAndStripQuotes()
    {
        // TEMP 在这台机器上是 8.3 短路径，所以按环境变量本身比对，不按 GetTempPath()
        var expanded = PathMapper.ExpandTargetPath(@"""%TEMP%\out""");

        Assert.Equal(Path.Combine(Environment.ExpandEnvironmentVariables("%TEMP%"), "out"), expanded);
        Assert.DoesNotContain('"', expanded);
    }
}
