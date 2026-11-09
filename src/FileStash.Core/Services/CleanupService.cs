using FileStash.Core.Abstractions;
using FileStash.Core.Utils;

namespace FileStash.Core.Services;

/// <summary>存储占用统计。</summary>
/// <param name="StashBytes">暂存内容占用。</param>
/// <param name="BackupBytes">覆盖前的备份占用。</param>
/// <param name="ExportBytes">拖出物化的临时文件占用。</param>
public sealed record StorageStats(long StashBytes, long BackupBytes, long ExportBytes);

/// <summary>清理：过期的历史、没人引用的 blob、拖出留下的临时目录。</summary>
public interface ICleanupService
{
    /// <summary>删除超过保留天数的历史操作（0 = 永久保留）。</summary>
    Task<int> DeleteExpiredHistoryAsync(int retentionDays, CancellationToken ct);

    /// <summary>删除没人引用的 blob。</summary>
    Task<int> DeleteUnreferencedBlobsAsync(CancellationToken ct);

    /// <summary>删除拖出留下的临时目录（默认 24 小时前的）。</summary>
    Task<int> DeleteExpiredExportsAsync(TimeSpan maxAge, CancellationToken ct);

    /// <summary>清掉 blobs\tmp 里遗留的半成品。</summary>
    Task<int> DeleteStaleTemporaryFilesAsync(TimeSpan maxAge, CancellationToken ct);

    /// <summary>存储占用统计。</summary>
    Task<StorageStats> GetStorageStatsAsync(CancellationToken ct);
}

/// <summary>清理服务实现。</summary>
public sealed class CleanupService : ICleanupService
{
    /// <summary>拖出物化目录的保留时长：1 小时（拖放拷贝完成后即无用，1 小时足够大文件跨盘拷贝完成）。</summary>
    public static readonly TimeSpan ExportRetention = TimeSpan.FromHours(1);

    private readonly IStashEntryRepository _entries;
    private readonly IOperationEntryRepository _operationEntries;
    private readonly IOperationRepository _operations;
    private readonly IBlobStore _blobStore;
    private readonly AppPaths _paths;

    public CleanupService(
        IStashEntryRepository entries,
        IOperationEntryRepository operationEntries,
        IOperationRepository operations,
        IBlobStore blobStore,
        AppPaths paths)
    {
        _entries = entries;
        _operationEntries = operationEntries;
        _operations = operations;
        _blobStore = blobStore;
        _paths = paths;
    }

    public async Task<int> DeleteExpiredHistoryAsync(int retentionDays, CancellationToken ct)
    {
        if (retentionDays <= 0)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var expired = await _operations.GetOlderThanAsync(cutoff, ct);

        foreach (var operation in expired)
        {
            // 条目上的外键是 ON DELETE CASCADE，操作删了条目跟着删
            await _operations.DeleteAsync(operation.Id, ct);
        }

        return expired.Count;
    }

    public async Task<int> DeleteUnreferencedBlobsAsync(CancellationToken ct)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 暂存内容引用的
        foreach (var sha in await _entries.GetReferencedSha256Async(ct))
        {
            referenced.Add(sha);
        }

        // 未过期历史里的备份要留着，否则撤不回来
        foreach (var backup in await _operationEntries.GetReferencedBackupSha256Async(ct))
        {
            referenced.Add(backup);
        }

        return await _blobStore.DeleteUnreferencedAsync(referenced, ct);
    }

    public Task<int> DeleteExpiredExportsAsync(TimeSpan maxAge, CancellationToken ct) =>
        Task.FromResult(DeleteExpiredDirectories(_paths.ExportDirectory, maxAge));

    public Task<int> DeleteStaleTemporaryFilesAsync(TimeSpan maxAge, CancellationToken ct)
    {
        var deleted = 0;

        if (!Directory.Exists(_paths.BlobTempDirectory))
        {
            return Task.FromResult(0);
        }

        var cutoff = DateTime.UtcNow - maxAge;

        foreach (var file in Directory.EnumerateFiles(_paths.BlobTempDirectory))
        {
            try
            {
                if (maxAge > TimeSpan.Zero && new FileInfo(file).LastWriteTimeUtc >= cutoff)
                {
                    continue;
                }

                var attributes = File.GetAttributes(file);

                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }

                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 还被占着，下一轮再说
            }
        }

        return Task.FromResult(deleted);
    }

    public Task<StorageStats> GetStorageStatsAsync(CancellationToken ct)
    {
        var stashBytes = 0L;
        var exportBytes = 0L;

        if (Directory.Exists(_paths.BlobDirectory))
        {
            var blobDir = new DirectoryInfo(_paths.BlobDirectory);
            foreach (var info in blobDir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (info.FullName.StartsWith(_paths.BlobTempDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                stashBytes += info.Length;
            }
        }

        if (Directory.Exists(_paths.ExportDirectory))
        {
            var exportDir = new DirectoryInfo(_paths.ExportDirectory);
            foreach (var info in exportDir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                exportBytes += info.Length;
            }
        }

        // 备份的体积要单算：它和暂存内容都存在 blob 里，但语义不同
        return Task.FromResult(new StorageStats(stashBytes, 0, exportBytes));
    }

    private static int DeleteExpiredDirectories(string root, TimeSpan maxAge)
    {
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var deleted = 0;
        var cutoff = DateTime.UtcNow - maxAge;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (maxAge > TimeSpan.Zero && new DirectoryInfo(directory).LastWriteTimeUtc >= cutoff)
                {
                    continue;
                }

                ClearReadOnlyRecursively(directory);
                Directory.Delete(directory, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 还被占着（用户可能正拖着呢），下一轮再说
            }
        }

        return deleted;
    }

    private static void ClearReadOnlyRecursively(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attributes = File.GetAttributes(file);

                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
