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
    public async Task QueuedPublicationMustProtectTheHashedTemporaryContent()
    {
        var source = CreateFile("source.txt", "verified source");
        var (hash, _) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);
        var publication = (SemaphoreSlim)typeof(BlobStore)
            .GetField("PublicationLock", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        await publication.WaitAsync(TestContext.Current.CancellationToken);
        Task<(string Sha256, long Size)>? publishing = null;
        Exception? modification = null;
        try
        {
            publishing = _store.PutFileAsync(source, TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => SourceIsReleased(source));
            var temporary = Assert.Single(Directory.EnumerateFiles(_paths.BlobTempDirectory));
            modification = Record.Exception(() => File.WriteAllText(temporary, "changed after hashing"));
        }
        finally { publication.Release(); }
        Assert.NotNull(publishing);
        await publishing;
        Assert.Equal(hash, await HashUtil.ComputeFileSha256Async(_store.GetBlobPath(hash), TestContext.Current.CancellationToken));
        Assert.True(modification is IOException or UnauthorizedAccessException);
        Assert.Empty(Directory.EnumerateFiles(_paths.BlobTempDirectory));
    }

    [Fact]
    public async Task CancelledQueuedPublicationMustReleaseAndRemoveItsTemporaryFile()
    {
        var source = CreateFile("source.txt", "verified source");
        var (hash, _) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);
        var publication = (SemaphoreSlim)typeof(BlobStore)
            .GetField("PublicationLock", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await publication.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var publishing = _store.PutFileAsync(source, cancellation.Token);
            await WaitUntilAsync(() => SourceIsReleased(source));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishing);
            Assert.Empty(Directory.EnumerateFiles(_paths.BlobTempDirectory));
            Assert.Equal(hash, await HashUtil.ComputeFileSha256Async(_store.GetBlobPath(hash), TestContext.Current.CancellationToken));
            Assert.True(File.GetAttributes(_store.GetBlobPath(hash)).HasFlag(FileAttributes.ReadOnly));
        }
        finally { publication.Release(); }
    }

    [Fact]
    public async Task CancelledFallbackMustRestoreReadOnlyAndReleasePublication()
    {
        var source = CreateFile("source.txt", "verified source");
        var (hash, _) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);
        var blob = _store.GetBlobPath(hash);
        File.SetAttributes(blob, FileAttributes.Normal);
        using (var file = File.OpenWrite(blob)) file.SetLength(256L * 1024 * 1024);
        File.SetAttributes(blob, FileAttributes.ReadOnly);
        await using (var reader = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var publishing = _store.PutFileAsync(source, cancellation.Token);
            await WaitUntilAsync(() => !File.GetAttributes(blob).HasFlag(FileAttributes.ReadOnly));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishing);
            Assert.True(File.GetAttributes(blob).HasFlag(FileAttributes.ReadOnly));
            Assert.Empty(Directory.EnumerateFiles(_paths.BlobTempDirectory));
        }
        await _store.PutFileAsync(source, TestContext.Current.CancellationToken);
        Assert.Equal(hash, await HashUtil.ComputeFileSha256Async(blob, TestContext.Current.CancellationToken));
    }

    private static bool SourceIsReleased(string source)
    {
        try { using var handle = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
        catch (IOException) { return false; }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "I/O checkpoint was not reached");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DeduplicationMustAtomicallyRepublishFreshHashedBytes()
    {
        var source = CreateFile("source.txt", "verified source");
        var (hash, _) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);
        var blob = _store.GetBlobPath(hash);
        var writer = new FileStash.Infrastructure.Native.AtomicFileWriter();
        var previousIdentity = writer.GetIdentity(blob);
        var stored = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);
        Assert.Equal(hash, stored.Sha256);
        Assert.NotEqual(previousIdentity, writer.GetIdentity(blob));
        Assert.Equal(hash, await HashUtil.ComputeFileSha256Async(blob, TestContext.Current.CancellationToken));
        Assert.True(File.GetAttributes(blob).HasFlag(FileAttributes.ReadOnly));
        Assert.Empty(Directory.EnumerateFiles(_paths.BlobTempDirectory));
    }

    [Fact]
    public async Task DeduplicationMustRetainVerifiedBlobWhenReaderDeniesReplacement()
    {
        var source = CreateFile("source.txt", "verified source");
        var (hash, _) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);
        await using var reader = new FileStream(_store.GetBlobPath(hash), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Equal(hash, (await _store.PutFileAsync(source, TestContext.Current.CancellationToken)).Sha256);
        Assert.True(File.GetAttributes(_store.GetBlobPath(hash)).HasFlag(FileAttributes.ReadOnly));
        Assert.Empty(Directory.EnumerateFiles(_paths.BlobTempDirectory));
    }

    [Fact]
    public async Task LockedCorruptBlobMustFailSafelyAndRecoverAfterUnlock()
    {
        var source = CreateFile("source.txt", "verified source");
        var (hash, _) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);
        var blob = _store.GetBlobPath(hash);
        File.SetAttributes(blob, FileAttributes.Normal);
        File.WriteAllText(blob, "corrupted");
        File.SetAttributes(blob, FileAttributes.ReadOnly);
        await using (var reader = new FileStream(blob, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(() => _store.PutFileAsync(source, TestContext.Current.CancellationToken));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.True(File.GetAttributes(blob).HasFlag(FileAttributes.ReadOnly));
            Assert.Empty(Directory.EnumerateFiles(_paths.BlobTempDirectory));
        }
        await _store.PutFileAsync(source, TestContext.Current.CancellationToken);
        Assert.Equal(hash, await HashUtil.ComputeFileSha256Async(blob, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CopyMustRejectCorruptContent()
    {
        var source = CreateFile("a.txt", "valid");
        var (hash, _) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);
        var blob = _store.GetBlobPath(hash);
        File.SetAttributes(blob, FileAttributes.Normal);
        File.WriteAllText(blob, "corrupted");
        await Assert.ThrowsAsync<IOException>(() =>
            _store.CopyBlobToAsync(hash, Path.Combine(_root, "copy.txt"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResnapshotMustRepairCorruptDeduplicatedBlob()
    {
        var source = CreateFile("a.txt", "valid");
        var (hash, _) = await _store.PutFileAsync(source, TestContext.Current.CancellationToken);
        var blob = _store.GetBlobPath(hash);
        File.SetAttributes(blob, FileAttributes.Normal);
        File.WriteAllText(blob, "corrupted");
        await _store.PutFileAsync(source, TestContext.Current.CancellationToken);
        Assert.Equal("valid", File.ReadAllText(blob));
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
    public async Task ConcurrentStoresMustPublishOnlyOneVerifiedBlob()
    {
        var source = CreateFile("source.txt", "concurrent stores");
        var other = new BlobStore(_paths, TimeSpan.Zero);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            (index % 2 == 0 ? _store : other).PutFileAsync(source, TestContext.Current.CancellationToken)));
        var hash = Assert.Single(results.Select(result => result.Sha256).Distinct());
        var files = Directory.EnumerateFiles(_paths.BlobDirectory, "*", SearchOption.AllDirectories).ToArray();
        Assert.Equal(_store.GetBlobPath(hash), Assert.Single(files));
        Assert.Equal(hash, await HashUtil.ComputeFileSha256Async(files[0], TestContext.Current.CancellationToken));
        Assert.True(File.GetAttributes(files[0]).HasFlag(FileAttributes.ReadOnly));
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
