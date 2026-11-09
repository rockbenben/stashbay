using System.Text;
using FileStash.Infrastructure.Native;

namespace FileStash.Core.Tests;

public sealed class AtomicFileWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AtomicFileWriter _writer = new();

    public AtomicFileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "filestash-atomicwriter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TempDirectory.DeleteRecursively(_tempDir);
    }

    [Fact]
    public async Task ReplaceAsync_WhenTargetDoesNotExist_MovesTemporaryToTarget()
    {
        var tempFile = Path.Combine(_tempDir, "temp.txt");
        var targetFile = Path.Combine(_tempDir, "target.txt");

        await File.WriteAllTextAsync(tempFile, "hello from temp", Encoding.UTF8, TestContext.Current.CancellationToken);

        var result = await _writer.ReplaceAsync(tempFile, targetFile, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.False(File.Exists(tempFile));
        Assert.True(File.Exists(targetFile));
        Assert.Equal("hello from temp", await File.ReadAllTextAsync(targetFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReplaceAsync_WhenTargetExists_ReplacesContentAtomically()
    {
        var tempFile = Path.Combine(_tempDir, "temp2.txt");
        var targetFile = Path.Combine(_tempDir, "target2.txt");

        await File.WriteAllTextAsync(targetFile, "initial content", Encoding.UTF8, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(tempFile, "updated content", Encoding.UTF8, TestContext.Current.CancellationToken);

        var result = await _writer.ReplaceAsync(tempFile, targetFile, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.False(File.Exists(tempFile));
        Assert.True(File.Exists(targetFile));
        Assert.Equal("updated content", await File.ReadAllTextAsync(targetFile, TestContext.Current.CancellationToken));
    }
}
