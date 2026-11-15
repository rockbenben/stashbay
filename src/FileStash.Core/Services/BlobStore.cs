using System.Buffers;
using System.Security.Cryptography;
using FileStash.Core.Abstractions;
using FileStash.Core.Resources;
using FileStash.Core.Utils;

namespace FileStash.Core.Services;

/// <summary>
/// 内容寻址存储。文件名 = SHA-256（小写十六进制），前 2 位为子目录。
/// </summary>
/// <remarks>
/// blob 写入后置为只读，防止误改；删除前必须先清掉只读属性，否则 File.Delete 抛 UnauthorizedAccessException。
/// </remarks>
public sealed class BlobStore : IBlobStore
{
    /// <summary>
    /// 清理未引用 blob 时的宽限期：只删除"最后写入时间早于它"的 blob。
    /// </summary>
    /// <remarks>
    /// blob 先落盘、行后提交，中间有一个窗口期；不设宽限期的话，
    /// 一次并发的"清理未引用数据"会把这个刚写好、还没入库的快照删掉。
    /// </remarks>
    public static readonly TimeSpan DefaultCleanupGracePeriod = TimeSpan.FromHours(1);

    // Serialize publication metadata across store instances. Application-level
    // StorageWriteLease provides the corresponding cross-process coordination.
    private static readonly SemaphoreSlim PublicationLock = new(1, 1);

    private readonly string _blobDirectory;
    private readonly string _tempDirectory;
    private readonly TimeSpan _cleanupGracePeriod;

    public BlobStore(AppPaths paths, TimeSpan? cleanupGracePeriod = null)
    {
        _blobDirectory = paths.BlobDirectory;
        _tempDirectory = paths.BlobTempDirectory;
        _cleanupGracePeriod = cleanupGracePeriod ?? DefaultCleanupGracePeriod;

        Directory.CreateDirectory(_blobDirectory);
        Directory.CreateDirectory(_tempDirectory);
    }

    public string GetBlobPath(string sha256)
    {
        if (!HashUtil.IsValidSha256(sha256))
        {
            throw new ArgumentException(Strings.Format(Strings.InvalidSha256, sha256), nameof(sha256));
        }

        return Path.Combine(_blobDirectory, sha256[..2], sha256);
    }

    public bool Exists(string sha256) => File.Exists(GetBlobPath(sha256));

    public async Task<(string Sha256, long Size)> PutFileAsync(string sourcePath, CancellationToken ct)
    {
        var tempPath = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N"));
        long total = 0;

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            await using var source = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, HashUtil.BufferSize, useAsync: true);
            await using var target = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, HashUtil.BufferSize, useAsync: true);
            var buffer = ArrayPool<byte>.Shared.Rent(HashUtil.BufferSize);
            int read;

            try
            {
                while ((read = await source.ReadAsync(buffer.AsMemory(0, HashUtil.BufferSize), ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            // Release the source once hashed, but keep the staged copy exclusive
            // throughout any publication wait so its verified bytes cannot change.
            await source.DisposeAsync();

            var sha256 = Convert.ToHexStringLower(hasher.GetHashAndReset());
            var finalPath = GetBlobPath(sha256);

            await PublicationLock.WaitAsync(ct);
            try
            {
                // Finish flushing and close only inside the publication critical
                // section; Windows replacement requires the staging handle closed.
                await target.DisposeAsync();
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

                try
                {
                    File.Move(tempPath, finalPath);
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                    // The fresh temporary copy has already been hashed. Publish those
                    // bytes atomically instead of rereading and hashing the previous blob.
                    var wasReadOnly = File.GetAttributes(finalPath).HasFlag(FileAttributes.ReadOnly);
                    try
                    {
                        ClearReadOnly(finalPath);
                        try { File.Replace(tempPath, finalPath, destinationBackupFileName: null); }
                        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                            && File.Exists(finalPath))
                        {
                            // External readers may deny replacement. Retain an existing
                            // blob only after independently verifying all of its content.
                            if (!string.Equals(await HashUtil.ComputeFileSha256Async(finalPath, ct),
                                    sha256, StringComparison.OrdinalIgnoreCase))
                                throw;
                            File.Delete(tempPath);
                        }
                    }
                    finally
                    {
                        if (wasReadOnly) MarkReadOnly(finalPath);
                    }
                }

                MarkReadOnly(finalPath);
                return (sha256, total);
            }
            finally { PublicationLock.Release(); }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public async Task CopyBlobToAsync(string sha256, string destinationPath, CancellationToken ct)
    {
        var source = GetBlobPath(sha256);

        if (!File.Exists(source))
        {
            throw new FileNotFoundException(Strings.Format(Strings.BlobNotFound, sha256), source);
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var destinationOpened = false;
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, HashUtil.BufferSize, useAsync: true);
            await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
                FileShare.None, HashUtil.BufferSize, useAsync: true);
            destinationOpened = true;
            var buffer = ArrayPool<byte>.Shared.Rent(HashUtil.BufferSize);
            try
            {
                int bytesRead;
                while ((bytesRead = await input.ReadAsync(buffer.AsMemory(0, HashUtil.BufferSize), ct)) > 0)
                {
                    hasher.AppendData(buffer, 0, bytesRead);
                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            if (!string.Equals(Convert.ToHexStringLower(hasher.GetHashAndReset()), sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new IOException(Strings.BlobIntegrityFailed);
        }
        catch
        {
            if (destinationOpened) TryDelete(destinationPath);
            throw;
        }

        ClearReadOnly(destinationPath);
    }

    public Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referenced, CancellationToken ct)
    {
        var deleted = 0;
        var now = DateTime.UtcNow;

        foreach (var directory in Directory.EnumerateDirectories(_blobDirectory))
        {
            if (string.Equals(directory, _tempDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileName(file);

                if (referenced.Contains(name))
                {
                    continue;
                }

                var info = new FileInfo(file);

                if (now - info.LastWriteTimeUtc < _cleanupGracePeriod)
                {
                    // 宽限期内：可能正处在"blob 已落盘、行尚未提交"的窗口里
                    continue;
                }

                if (TryDelete(file))
                {
                    deleted++;
                }
            }
        }

        // 顺带清掉临时目录里遗留的半成品
        foreach (var file in Directory.EnumerateFiles(_tempDirectory))
        {
            var info = new FileInfo(file);

            if (now - info.LastWriteTimeUtc < _cleanupGracePeriod)
            {
                continue;
            }

            TryDelete(file);
        }

        return Task.FromResult(deleted);
    }

    public long GetTotalSize()
    {
        long total = 0;

        foreach (var directory in Directory.EnumerateDirectories(_blobDirectory))
        {
            if (string.Equals(directory, _tempDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                total += new FileInfo(file).Length;
            }
        }

        return total;
    }

    private static void MarkReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
        }
        catch (IOException)
        {
            // 属性设置失败不影响内容正确性
        }
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (IOException)
        {
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            ClearReadOnly(path);
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            // 被占用：下一轮再来
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
