namespace FileStash.Core.Abstractions;

/// <summary>内容寻址存储。暂存快照与覆盖前备份都存在这里（相同内容只存一份）。</summary>
public interface IBlobStore
{
    /// <summary>流式计算哈希并写入 blob，返回 (sha256, size)。</summary>
    Task<(string Sha256, long Size)> PutFileAsync(string sourcePath, CancellationToken ct);

    /// <summary>blob 的完整路径（不保证存在）。</summary>
    string GetBlobPath(string sha256);

    bool Exists(string sha256);

    /// <summary>把 blob 的内容复制到目标路径（不复制只读属性）。</summary>
    Task CopyBlobToAsync(string sha256, string destinationPath, CancellationToken ct);

    /// <summary>删除没有被引用的 blob，返回删除个数。</summary>
    Task<int> DeleteUnreferencedAsync(IReadOnlySet<string> referenced, CancellationToken ct);

    /// <summary>全部 blob 占用的字节数。</summary>
    long GetTotalSize();
}
