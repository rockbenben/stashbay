using FileStash.Core.Services;
using FileStash.Core.Utils;

namespace FileStash.Core.Tests;

public sealed class BlobStoreTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;
    private readonly BlobStore _store;

    public BlobStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "filestash-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root, _root);
        _paths.EnsureDirectories();

        // 宽限期设 0：测试里不想等 1 小时
        _store = new BlobStore(_paths, TimeSpan.Zero);
    }

    public void Dispose()
    {
        TempDirectory.DeleteRecursively(_root);
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task PutFileAsync_ShouldStoreBlobAndReturnHashAndSize()
    {
        var source = CreateFile("a.txt", "hello");

        var (sha256, size) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(64, sha256.Length);
        Assert.Equal(5, size);
        Assert.True(_store.Exists(sha256));
        Assert.Equal("hello", await File.ReadAllTextAsync(_store.GetBlobPath(sha256), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PutFileAsync_ShouldProduceSameHashForSameContent()
    {
        var first = CreateFile("a.txt", "same content");
        var second = CreateFile("b.txt", "same content");

        var (sha1, _) = await _store.PutFileAsync(first, TestContext.Current.CancellationToken);
        var (sha2, _) = await _store.PutFileAsync(second, TestContext.Current.CancellationToken);

        Assert.Equal(sha1, sha2);
    }

    [Fact]
    public async Task PutFileAsync_ShouldDeduplicateContent()
    {
        var first = CreateFile("a.txt", "same content");
        var second = CreateFile("b.txt", "same content");

        var (sha1, _) = await _store.PutFileAsync(first, TestContext.Current.CancellationToken);
        await _store.PutFileAsync(second, TestContext.Current.CancellationToken);

        var blobs = Directory
            .EnumerateFiles(_paths.BlobDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetDirectoryName(path) != _paths.BlobTempDirectory)
            .ToList();

        Assert.Single(blobs);
        Assert.Equal(sha1, Path.GetFileName(blobs[0]));
    }

    [Fact]
    public async Task PutFileAsync_ShouldMarkBlobReadOnly()
    {
        var source = CreateFile("a.txt", "hello");

        var (sha256, _) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);

        var attributes = File.GetAttributes(_store.GetBlobPath(sha256));
        Assert.True(attributes.HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task PutFileAsync_ShouldLeaveNoTempFiles()
    {
        var source = CreateFile("a.txt", "hello");

        await _store.PutFileAsync(source, TestContext.Current.CancellationToken);

        Assert.Empty(Directory.EnumerateFiles(_paths.BlobTempDirectory));
    }

    [Fact]
    public async Task ConcurrentWritesOfSameContent_ShouldEndUpWithOneBlob()
    {
        var source = CreateFile("a.txt", "concurrent content");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _store.PutFileAsync(source, TestContext.Current.CancellationToken)));

        Assert.Single(results.Select(result => result.Sha256).Distinct());

        var blobs = Directory
            .EnumerateFiles(_paths.BlobDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetDirectoryName(path) != _paths.BlobTempDirectory)
            .ToList();

        Assert.Single(blobs);
        Assert.Empty(Directory.EnumerateFiles(_paths.BlobTempDirectory));
    }

    [Fact]
    public async Task CopyBlobToAsync_ShouldCopyContentWithoutReadOnlyAttribute()
    {
        var source = CreateFile("a.txt", "payload");
        var (sha256, _) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);

        var destination = Path.Combine(_root, "out", "copied.txt");
        await _store.CopyBlobToAsync(sha256, destination, TestContext.Current.CancellationToken);

        Assert.Equal("payload", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));

        // blob 的只读只是"别误改"的保护，不该传染给目标
        var attributes = File.GetAttributes(destination);
        Assert.False(attributes.HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task CopyBlobToAsync_ShouldOverwriteExistingDestination()
    {
        var source = CreateFile("a.txt", "new content");
        var (sha256, _) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);

        var destination = Path.Combine(_root, "copied.txt");
        await File.WriteAllTextAsync(destination, "old content", TestContext.Current.CancellationToken);

        await _store.CopyBlobToAsync(sha256, destination, TestContext.Current.CancellationToken);

        Assert.Equal("new content", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteUnreferencedAsync_ShouldKeepReferencedBlobs()
    {
        var keepSource = CreateFile("keep.txt", "keep");
        var dropSource = CreateFile("drop.txt", "drop");

        var (keepSha, _) = await _store.PutFileAsync(keepSource, TestContext.Current.CancellationToken);
        var (dropSha, _) = await _store.PutFileAsync(dropSource, TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteUnreferencedAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { keepSha },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        Assert.True(_store.Exists(keepSha));
        Assert.False(_store.Exists(dropSha));
    }

    [Fact]
    public async Task DeleteUnreferencedAsync_ShouldRespectGracePeriod()
    {
        var source = CreateFile("fresh.txt", "fresh");
        var (sha256, _) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);

        // 宽限期 1 小时的 store：刚写的 blob 不能被清掉
        var carefulStore = new BlobStore(_paths);

        var deleted = await carefulStore.DeleteUnreferencedAsync(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, deleted);
        Assert.True(carefulStore.Exists(sha256));
    }

    [Fact]
    public async Task GetTotalSize_ShouldSumBlobSizes()
    {
        var source = CreateFile("a.txt", "1234567890");

        await _store.PutFileAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(10, _store.GetTotalSize());
    }

    [Fact]
    public void GetBlobPath_ShouldRejectInvalidHash()
    {
        Assert.Throws<ArgumentException>(() => _store.GetBlobPath("not-a-hash"));
    }
}
