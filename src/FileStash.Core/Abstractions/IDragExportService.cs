using FileStash.Core.Models;

namespace FileStash.Core.Abstractions;

/// <summary>
/// 把暂存项物化成真实文件（拖出 / 复制到剪贴板用）。
/// </summary>
public interface IDragExportService
{
    /// <summary>
    /// 物化一组暂存项，返回顶层路径列表（拖放与剪贴板要的就是这个）。
    /// </summary>
    Task<IReadOnlyList<string>> MaterializeAsync(IReadOnlyList<StashItem> items, CancellationToken ct);
}
