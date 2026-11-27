using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Utils;

namespace FileStash.Core.Abstractions;

/// <summary>暂存服务：组的增删改、加入暂存、刷新快照、设置根目录、移动、源状态检查。</summary>
public interface IStashService
{
    /// <summary>当暂存组列表发生变更（新增、改名、删除、回收）时触发。</summary>
    event Action? GroupsChanged;

    /// <summary>创建新的暂存组。</summary>
    /// <param name="name">组名称。</param>
    /// <param name="kind">组类型（默认为 Saved 已保存，可选 Temporary 临时搁架或 Default 默认组）。</param>
    Task<StashGroup> CreateGroupAsync(string name, StashGroupKind kind = StashGroupKind.Saved);

    /// <summary>仅当指定暂存组为临时组且内容为空时，将其原子删除。</summary>
    Task<bool> DeleteEmptyTemporaryGroupAsync(string groupId);

    /// <summary>批量清理所有未在排除列表中的孤儿空临时暂存组。</summary>
    Task<int> DeleteEmptyTemporaryGroupsAsync(IReadOnlyCollection<string> excludedGroupIds, CancellationToken ct = default);

    /// <summary>重命名指定暂存组。</summary>
    Task RenameGroupAsync(string groupId, string name);

    /// <summary>删除指定的非默认暂存组及其包含的所有暂存项。</summary>
    Task DeleteGroupAsync(string groupId);

    /// <summary>清空所有暂存项并删除非默认组。</summary>
    Task ClearAllGroupsAsync();

    /// <summary>将一个或多个文件/目录路径加入指定暂存组并生成快照。</summary>
    Task<AddResult> AddAsync(
        string groupId,
        IReadOnlyList<string> paths,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct);

    /// <summary>根据源文件最新内容重新生成暂存项快照。</summary>
    Task RefreshSnapshotAsync(
        IReadOnlyList<string> itemIds,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct);

    /// <summary>更改指定暂存项的相对根目录基准。</summary>
    Task SetRootAsync(IReadOnlyList<string> itemIds, string newRoot);

    /// <summary>从暂存箱中批量移除指定的暂存项。</summary>
    Task RemoveItemsAsync(IReadOnlyList<string> itemIds);

    /// <summary>将暂存项移动到目标暂存组中。</summary>
    Task MoveItemsAsync(IReadOnlyList<string> itemIds, string targetGroupId);

    /// <summary>检查暂存组内所有项在源磁盘路径的实时存在与变更状态。</summary>
    Task<IReadOnlyList<ItemSourceStatus>> CheckSourceStatusAsync(string groupId, CancellationToken ct);
}
