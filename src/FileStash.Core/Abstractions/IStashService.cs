using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Utils;

namespace FileStash.Core.Abstractions;

/// <summary>暂存服务：组的增删改、加入暂存、刷新快照、设置根目录、移动、源状态检查。</summary>
public interface IStashService
{
    event Action? GroupsChanged;

    Task<StashGroup> CreateGroupAsync(string name, StashGroupKind kind = StashGroupKind.Saved);

    Task<bool> DeleteEmptyTemporaryGroupAsync(string groupId);

    Task<int> DeleteEmptyTemporaryGroupsAsync(IReadOnlyCollection<string> excludedGroupIds, CancellationToken ct = default);

    Task RenameGroupAsync(string groupId, string name);

    Task DeleteGroupAsync(string groupId);

    Task<AddResult> AddAsync(
        string groupId,
        IReadOnlyList<string> paths,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct);

    Task RefreshSnapshotAsync(
        IReadOnlyList<string> itemIds,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct);

    Task SetRootAsync(IReadOnlyList<string> itemIds, string newRoot);

    Task RemoveItemsAsync(IReadOnlyList<string> itemIds);

    Task MoveItemsAsync(IReadOnlyList<string> itemIds, string targetGroupId);

    Task<IReadOnlyList<ItemSourceStatus>> CheckSourceStatusAsync(string groupId, CancellationToken ct);
}
