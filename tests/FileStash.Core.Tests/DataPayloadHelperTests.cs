using System.IO;
using System.Windows;
using FileStash.App.Services;

namespace FileStash.Core.Tests;

public sealed class DataPayloadHelperTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "stashbay-test-" + Guid.NewGuid().ToString("N"));

    public DataPayloadHelperTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TempDirectory.DeleteRecursively(_tempDir);
    }

    [Fact]
    public void IsSupported_WithNull_ShouldReturnFalse()
    {
        Assert.False(DataPayloadHelper.IsSupported(null));
    }

    [Fact]
    public void IsSupported_WithText_ShouldReturnTrue()
    {
        var data = new DataObject(DataFormats.UnicodeText, "hello stash");
        Assert.True(DataPayloadHelper.IsSupported(data));
    }

    [Fact]
    public void IsSupported_WithFiles_ShouldReturnTrue()
    {
        var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\test.txt" });
        Assert.True(DataPayloadHelper.IsSupported(data));
    }

    [Fact]
    public void ExtractPayloadPaths_WithFiles_ShouldReturnDirectPaths()
    {
        var files = new[] { @"C:\foo.txt", @"C:\bar.png" };
        var data = new DataObject(DataFormats.FileDrop, files);

        var result = DataPayloadHelper.ExtractPayloadPaths(data, _tempDir);

        Assert.Equal(files, result);
    }

    [Fact]
    public void ExtractPayloadPaths_WithText_ShouldCreateSnippetFile()
    {
        const string text = "这是一段用于测试的选中文本片段";
        var data = new DataObject(DataFormats.UnicodeText, text);

        var result = DataPayloadHelper.ExtractPayloadPaths(data, _tempDir);

        Assert.Single(result);
        var file = result[0];
        Assert.True(File.Exists(file));
        Assert.EndsWith(".txt", file);
        Assert.Equal(text, File.ReadAllText(file));
    }

    [Fact]
    public void ExtractPayloadPaths_WithUrlText_ShouldCreateUrlShortcutFile()
    {
        const string url = "https://github.com/test/repo";
        var data = new DataObject(DataFormats.UnicodeText, url);

        var result = DataPayloadHelper.ExtractPayloadPaths(data, _tempDir);

        Assert.Single(result);
        var file = result[0];
        Assert.True(File.Exists(file));
        Assert.EndsWith(".url", file);
        var content = File.ReadAllText(file);
        Assert.Contains("[InternetShortcut]", content);
        Assert.Contains($"URL={url}", content);
    }

    [Fact]
    public void SaveTextSnippetOrLink_WithEmptyString_ShouldReturnNull()
    {
        Assert.Null(DataPayloadHelper.SaveTextSnippetOrLink("", _tempDir));
        Assert.Null(DataPayloadHelper.SaveTextSnippetOrLink("   ", _tempDir));
    }
}
