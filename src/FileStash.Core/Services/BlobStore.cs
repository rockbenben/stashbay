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
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             HashUtil.BufferSize,
                             useAsync: true))
            await using (var target = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             HashUtil.BufferSize,
                             useAsync: true))
            {
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
            }

            var sha256 = Convert.ToHexStringLower(hasher.GetHashAndReset());
            var finalPath = GetBlobPath(sha256);

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            if (File.Exists(finalPath))
            {
                // 同样的内容已经存过了：临时文件直接丢
                File.Delete(tempPath);
            }
            else
            {
                try
                {
                    File.Move(tempPath, finalPath);
                }
                catch (IOException)
                {
                    // 并发写入同一内容：别人先落地了，丢掉自己这份即可
                    File.Delete(tempPath);
                }
            }

            MarkReadOnly(finalPath);
            return (sha256, total);
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

        // 手工复制而不是 File.Copy：File.Copy 会把源文件的只读属性一起带过去，
        // 落地的目标就变成只读文件了 —— blob 的只读只是"别误改"的保护，不该传染给目标。
        await using (var input = new FileStream(
                         source,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete,
                         HashUtil.BufferSize,
                         useAsync: true))
        await using (var output = new FileStream(
                         destinationPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         HashUtil.BufferSize,
                         useAsync: true))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(HashUtil.BufferSize);
            try
            {
                int bytesRead;
                while ((bytesRead = await input.ReadAsync(buffer.AsMemory(0, HashUtil.BufferSize), ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
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
