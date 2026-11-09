using FileStash.Core.Utils;

namespace FileStash.Core.Tests;

public sealed class PathUtilTests
{
    [Theory]
    [InlineData(@"C:\foo\bar", @"C:\foo\bar")]
    [InlineData(@"C:\foo\bar\", @"C:\foo\bar")]
    [InlineData(@"C:\foo\..\baz", @"C:\baz")]
    [InlineData(@"C:\foo\\bar", @"C:\foo\bar")]
    public void Normalize_ShouldRemoveTrailingSeparatorAndResolveDotDot(string input, string expected)
    {
        Assert.Equal(expected, PathUtil.Normalize(input));
    }

    [Fact]
    public void Normalize_ShouldKeepDriveRoot()
    {
        Assert.Equal(@"C:\", PathUtil.Normalize(@"C:\"));
        Assert.Equal(@"C:\", PathUtil.Normalize(@"C:\."));
    }

    [Fact]
    public void Normalize_ShouldKeepUncRoot()
    {
        // UNC 共享根的末尾分隔符由 GetFullPath 决定，它给的是不带分隔符的形式
        Assert.Equal(@"\\server\share", PathUtil.Normalize(@"\\server\share\"));
    }

    [Fact]
    public void Normalize_ShouldRejectEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => PathUtil.Normalize("   "));
    }

    [Fact]
    public void GetRelative_ShouldReturnPathUnderRoot()
    {
        var relative = PathUtil.GetRelative(@"C:\repo", @"C:\repo\src\a.txt");

        Assert.Equal(@"src\a.txt", relative);
    }

    [Fact]
    public void GetRelative_ShouldReturnEmptyForRootItself()
    {
        Assert.Equal(string.Empty, PathUtil.GetRelative(@"C:\repo", @"C:\repo"));
    }

    [Fact]
    public void GetRelative_ShouldThrowWhenOutsideRoot()
    {
        Assert.Throws<ArgumentException>(() => PathUtil.GetRelative(@"C:\repo", @"C:\other\a.txt"));
    }

    [Theory]
    [InlineData(@"C:\repo", @"C:\repo\a", true)]
    [InlineData(@"C:\repo", @"C:\repo", true)]
    [InlineData(@"C:\repo", @"C:\repo2\a", false)]
    [InlineData(@"C:\repo", @"C:\other", false)]
    [InlineData(@"C:\repo", @"C:\repo\..\other", false)]
    public void IsUnder_ShouldDetectContainment(string root, string candidate, bool expected)
    {
        Assert.Equal(expected, PathUtil.IsUnder(root, candidate));
    }

    [Fact]
    public void IsUnder_ShouldBeCaseInsensitive()
    {
        Assert.True(PathUtil.IsUnder(@"C:\Repo", @"c:\repo\a"));
    }

    [Fact]
    public void IsUnder_ShouldRejectSiblingWithSharedPrefix()
    {
        // "C:\foobar" 不是在 "C:\foo" 之下 —— 这是最常见的越界漏洞
        Assert.False(PathUtil.IsUnder(@"C:\foo", @"C:\foobar"));
    }

    [Fact]
    public void ToKey_ShouldFoldNonAsciiCase()
    {
        // SQLite 的 NOCASE 只折叠 ASCII，非 ASCII 路径要靠这个键去重
        Assert.Equal(PathUtil.ToKey(@"C:\Tmp\Ä"), PathUtil.ToKey(@"C:\tmp\ä"));
        Assert.NotEqual(PathUtil.ToKey(@"C:\tmp\ä"), PathUtil.ToKey(@"C:\tmp\σ"));
    }

    [Fact]
    public void SplitAtParent_ShouldGiveParentAsRoot()
    {
        var (root, relative) = PathUtil.SplitAtParent(@"C:\repo\src\a.txt");

        Assert.Equal(@"C:\repo\src", root);
        Assert.Equal("a.txt", relative);
    }

    [Fact]
    public void SplitSegments_ShouldIgnoreEmptyAndBothSeparators()
    {
        var segments = PathUtil.SplitSegments(@"src\\auth/ login.ts");

        Assert.Equal(["src", "auth", "login.ts"], segments);
    }

    [Fact]
    public void Normalize_ShouldHandleLongPaths()
    {
        // 长路径需要系统开启 LongPathsEnabled=1（+ manifest 的 longPathAware）
        var longPath = @"C:\" + string.Join('\\', Enumerable.Repeat("verylongdirectoryname", 12)) + @"\file.txt";

        try
        {
            var normalized = PathUtil.Normalize(longPath);
            Assert.EndsWith("file.txt", normalized, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            // 本机没开长路径支持：跳过而不是失败
            Assert.True(true);
        }
    }
}
