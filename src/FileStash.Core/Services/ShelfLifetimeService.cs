using FileStash.Core.Abstractions;
using FileStash.Core.Models;

namespace FileStash.Core.Services;

/// <summary>搁架生命周期管理：仅回收空的临时暂存组，已填充内容或用户保存的组永不过期。</summary>
public sealed class ShelfLifetimeService(IStashGroupRepository groups, IStashService stash)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<object, string> _openShelves = [];

    /// <summary>为新搁架窗口预留并创建临时暂存组。</summary>
    public async Task<StashGroup> CreateTemporaryAsync(object reservation, string name)
    {
        await _gate.WaitAsync();
        try
        {
            var group = await stash.CreateGroupAsync(name, StashGroupKind.Temporary);
            _openShelves[reservation] = group.Id;
            return group;
        }
        finally { _gate.Release(); }
    }

    /// <summary>搁架窗口初始化就绪后，将预留的临时组归属转移至具体窗口对象。</summary>
    public async Task CompleteCreationAsync(object reservation, object window)
    {
        await _gate.WaitAsync();
        try
        {
            if (_openShelves.TryGetValue(reservation, out var reserved)
                && _openShelves.TryGetValue(window, out var active) && active == reserved)
                _openShelves.Remove(reservation);
        }
        finally { _gate.Release(); }
    }

    /// <summary>绑定或更新窗口当前展示的暂存组；若脱钩的旧临时组为空且无其他窗口使用，则立即回收。</summary>
    public async Task SetWindowGroupAsync(object window, string? groupId)
    {
        await _gate.WaitAsync();
        try
        {
            _openShelves.Remove(window, out var previous);
            if (groupId is not null) _openShelves[window] = groupId;
            if (previous is not null && previous != groupId && !_openShelves.ContainsValue(previous))
            {
                await SetClosedAtAsync(previous, DateTimeOffset.UtcNow);
                await stash.DeleteEmptyTemporaryGroupAsync(previous);
            }
            if (groupId is not null) await SetClosedAtAsync(groupId, null);
        }
        finally { _gate.Release(); }
    }

    /// <summary>批量清理所有未被任何打开窗口引用的空临时暂存组。</summary>
    public async Task<int> DeleteEmptyAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await stash.DeleteEmptyTemporaryGroupsAsync(_openShelves.Values.Distinct().ToArray(), ct); }
        finally { _gate.Release(); }
    }

    private async Task SetClosedAtAsync(string id, DateTimeOffset? closedAt)
    {
        var group = await groups.GetAsync(id, CancellationToken.None);
        if (group is null) return;
        group.ClosedAt = closedAt;
        await groups.UpdateAsync(group, CancellationToken.None);
    }
}
