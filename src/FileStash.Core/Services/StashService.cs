using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Resources;
using FileStash.Core.Utils;

namespace FileStash.Core.Services;

/// <summary>暂存服务实现。</summary>
public sealed class StashService : IStashService
{
    private readonly IStashGroupRepository _groups;
    private readonly IStashItemRepository _items;
    private readonly IStashEntryRepository _entries;
    private readonly IBlobStore _blobStore;
    private readonly IGitService _git;
    private readonly ISettingsService _settings;

    public StashService(
        IStashGroupRepository groups,
        IStashItemRepository items,
        IStashEntryRepository entries,
        IBlobStore blobStore,
        IGitService git,
        ISettingsService settings)
    {
        _groups = groups;
        _items = items;
        _entries = entries;
        _blobStore = blobStore;
        _git = git;
        _settings = settings;
    }

    public event Action? GroupsChanged;

    public async Task<StashGroup> CreateGroupAsync(string name, StashGroupKind kind = StashGroupKind.Saved)
    {
        await using var storageLease = await StorageWriteLease.AcquireAsync(_blobStore, CancellationToken.None);
        if (kind == StashGroupKind.Default)
        {
            var existing = (await _groups.GetAllAsync(CancellationToken.None)).FirstOrDefault(group => group.Kind == StashGroupKind.Default);
            if (existing is not null) return existing;
        }
        var now = DateTimeOffset.UtcNow;

        var group = new StashGroup
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? Strings.NewGroupName : name.Trim(),
            Kind = kind,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _groups.AddAsync(group, CancellationToken.None);
        return group;
    }

    public async Task RenameGroupAsync(string groupId, string name)
    {
        await using var storageLease = await StorageWriteLease.AcquireAsync(_blobStore, CancellationToken.None);
        var group = await _groups.GetAsync(groupId, CancellationToken.None);

        if (group is null)
        {
            return;
        }

        group.Name = string.IsNullOrWhiteSpace(name) ? group.Name : name.Trim();
        group.UpdatedAt = DateTimeOffset.UtcNow;

        await _groups.UpdateAsync(group, CancellationToken.None);
    }

    public async Task DeleteGroupAsync(string groupId)
    {
        await using var storageLease = await StorageWriteLease.AcquireAsync(_blobStore, CancellationToken.None);
        await _groups.DeleteAsync(groupId, CancellationToken.None);
    }

    public async Task<AddResult> AddAsync(
        string groupId,
        IReadOnlyList<string> paths,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct)
    {
        await using var storageLease = await StorageWriteLease.AcquireAsync(_blobStore, ct);
        if (await _groups.GetAsync(groupId, ct) is null)
        {
            var fallback = (await _groups.GetAllAsync(ct)).FirstOrDefault(group => group.Kind == StashGroupKind.Default);
            if (fallback is null)
            {
                fallback = new StashGroup { Id = Guid.NewGuid().ToString("N"), Name = Strings.DefaultGroupName,
                    Kind = StashGroupKind.Default, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
                await _groups.AddAsync(fallback, ct);
            }
            groupId = fallback.Id;
        }
        var added = new List<StashItem>();
        var duplicates = new List<string>();
        var warnings = new List<string>();
        var failed = new List<string>();

        var total = paths.Count;
        var done = 0;

        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();

            var normalized = PathUtil.Normalize(path);

            // 同组里已经有同一个源路径 → 交给 UI 问"更新快照 / 跳过"
            var existing = await _items.GetBySourcePathAsync(groupId, normalized, ct);

            if (existing is not null)
            {
                duplicates.Add(normalized);
                progress?.Report(new ProgressInfo(++done, total, normalized));
                continue;
            }

            var isDirectory = Directory.Exists(normalized);
            var isFile = File.Exists(normalized);

            if (!isDirectory && !isFile)
            {
                failed.Add(Strings.Format(Strings.PathNotFound, normalized));
                progress?.Report(new ProgressInfo(++done, total, normalized));
                continue;
            }

            var item = await AddOneAsync(groupId, normalized, isDirectory, progress, ct, warnings, failed);
            progress?.Report(new ProgressInfo(++done, total, normalized));

            if (item is not null)
            {
                added.Add(item);
            }
        }

        return new AddResult
        {
            Added = added,
            Duplicates = duplicates,
            Warnings = warnings,
            Failed = failed,
        };
    }

    private async Task<StashItem?> AddOneAsync(
        string groupId,
        string sourcePath,
        bool isDirectory,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct,
        List<string> warnings,
        List<string> failed)
    {
        // 根目录：仓库内取仓库根，否则取父目录（子路径 = 文件 / 文件夹名）
        var (root, rootSource) = await ResolveRootAsync(sourcePath, ct);
        var relativePath = PathUtil.GetRelative(root, sourcePath);

        var enumerated = isDirectory
            ? await FolderEnumerator.EnumerateAsync(root, sourcePath, _settings.Current.ExcludeGlobs, ct)
            : new EnumerationResult(
                [new EnumeratedEntry(relativePath, sourcePath, false, SafeLength(sourcePath), SafeLastWrite(sourcePath))],
                []);

        warnings.AddRange(enumerated.Warnings);

        var entries = new List<StashEntry>();
        var fileCount = 0;
        long totalSize = 0;
        var processed = 0;

        // 仓库内文件夹可以只暂存未被忽略的文件（respectGitIgnore）
        var candidates = await FilterCandidatesAsync(enumerated.Entries, rootSource, ct);

        foreach (var entry in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
            {
                entries.Add(new StashEntry
                {
                    ItemId = string.Empty,   // 插入时统一补上
                    RelativePath = entry.RelativePath,
                    IsDirectory = true,
                    Sha256 = null,
                    Size = 0,
                    SourceLastWriteUtc = null,
                });

                progress?.Report(new ProgressInfo(processed, enumerated.Entries.Count, entry.FullPath));
                continue;
            }

            try
            {
                var zone = await ZoneIdentifier.ReadAsync(entry.FullPath, ct);
                var (sha256, size) = await _blobStore.PutFileAsync(entry.FullPath, ct);

                entries.Add(new StashEntry
                {
                    ItemId = string.Empty,
                    RelativePath = entry.RelativePath,
                    IsDirectory = false,
                    Sha256 = sha256,
                    ZoneIdentifier = zone,
                    Size = size,
                    SourceLastWriteUtc = new DateTimeOffset(DateTime.SpecifyKind(entry.LastWriteTimeUtc, DateTimeKind.Utc)),
                });

                fileCount++;
                totalSize += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件失败不影响其余，最后汇总提示
                failed.Add(Strings.Format(Strings.PathWithError, entry.FullPath, ex.Message));
            }

            processed++;
            progress?.Report(new ProgressInfo(processed, enumerated.Entries.Count, entry.FullPath));
        }

        if (!isDirectory && entries.Count == 0)
            return null;

        var now = DateTimeOffset.UtcNow;

        var item = new StashItem
        {
            Id = Guid.NewGuid().ToString("N"),
            GroupId = groupId,
            Kind = isDirectory ? StashItemKind.Folder : StashItemKind.File,
            DisplayName = Path.GetFileName(sourcePath),
            SourcePath = sourcePath,
            SourcePathKey = PathUtil.ToKey(sourcePath),
            RootPath = root,
            RelativePath = relativePath,
            GitRepoRoot = rootSource,
            GitBranch = rootSource is null ? null : await _git.GetCurrentBranchAsync(rootSource, ct),
            GitCommit = rootSource is null ? null : await _git.GetHeadCommitAsync(rootSource, ct),
            FileCount = fileCount,
            TotalSize = totalSize,
            AddedAt = now,
            SnapshotAt = now,
        };

        foreach (var entry in entries)
        {
            entry.ItemId = item.Id;
        }

        await _items.SaveSnapshotAsync(item, entries, create: true, ct);

        return item;
    }

    /// <summary>
    /// 根目录确定：仓库内取仓库根，否则取父目录。
    /// </summary>
    /// <returns>(根目录, 仓库根或 null)</returns>
    public async Task<(string Root, string? RepoRoot)> ResolveRootAsync(string sourcePath, CancellationToken ct)
    {
        if (_git.IsAvailable)
        {
            var repoRoot = await _git.GetRepoRootAsync(sourcePath, ct);

            if (!string.IsNullOrEmpty(repoRoot))
            {
                return (PathUtil.Normalize(repoRoot), PathUtil.Normalize(repoRoot));
            }
        }

        var (root, _) = PathUtil.SplitAtParent(sourcePath);
        return (root, null);
    }

    public async Task RefreshSnapshotAsync(
        IReadOnlyList<string> itemIds,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct)
    {
        await using var storageLease = await StorageWriteLease.AcquireAsync(_blobStore, ct);
        var items = await _items.GetByIdsAsync(itemIds, ct);

        var done = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var sourcePath = item.SourcePath;
            var isDirectory = Directory.Exists(sourcePath);
            var isFile = File.Exists(sourcePath);

            if (!isDirectory && !isFile)
            {
                done++;
                progress?.Report(new ProgressInfo(done, items.Count, sourcePath));
                continue;
            }

            var root = item.RootPath;
            var warnings = new List<string>();

            var enumerated = isDirectory
                ? await FolderEnumerator.EnumerateAsync(root, sourcePath, _settings.Current.ExcludeGlobs, ct)
                : new EnumerationResult(
                    [
                        new EnumeratedEntry(
                            item.RelativePath,
                            sourcePath,
                            false,
                            SafeLength(sourcePath),
                            SafeLastWrite(sourcePath)),
                    ],
                    []);

            var entries = new List<StashEntry>();
            var fileCount = 0;
            long totalSize = 0;

            warnings.AddRange(enumerated.Warnings);
            var candidates = await FilterCandidatesAsync(enumerated.Entries, item.GitRepoRoot, ct);
            foreach (var entry in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (entry.IsDirectory)
                {
                    entries.Add(new StashEntry
                    {
                        ItemId = item.Id,
                        RelativePath = entry.RelativePath,
                        IsDirectory = true,
                        Sha256 = null,
                        Size = 0,
                        SourceLastWriteUtc = null,
                    });

                    continue;
                }

                try
                {
                    var zone = await ZoneIdentifier.ReadAsync(entry.FullPath, ct);
                    var (sha256, size) = await _blobStore.PutFileAsync(entry.FullPath, ct);

                    entries.Add(new StashEntry
                    {
                        ItemId = item.Id,
                        RelativePath = entry.RelativePath,
                        IsDirectory = false,
                        Sha256 = sha256,
                        ZoneIdentifier = zone,
                        Size = size,
                        SourceLastWriteUtc = new DateTimeOffset(DateTime.SpecifyKind(entry.LastWriteTimeUtc, DateTimeKind.Utc)),
                    });

                    fileCount++;
                    totalSize += size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add(Strings.Format(Strings.PathWithError, entry.FullPath, ex.Message));
                }
            }

            if (warnings.Count > 0)
                throw new IOException(string.Join(Environment.NewLine, warnings));

            item.Kind = isDirectory ? StashItemKind.Folder : StashItemKind.File;
            item.FileCount = fileCount;
            item.TotalSize = totalSize;
            item.SnapshotAt = DateTimeOffset.UtcNow;

            if (item.GitRepoRoot is not null)
            {
                item.GitBranch = await _git.GetCurrentBranchAsync(item.GitRepoRoot, ct);
                item.GitCommit = await _git.GetHeadCommitAsync(item.GitRepoRoot, ct);
            }

            await _items.SaveSnapshotAsync(item, entries, create: false, ct);

            done++;
            progress?.Report(new ProgressInfo(done, items.Count, sourcePath));
        }
    }

    public async Task SetRootAsync(IReadOnlyList<string> itemIds, string newRoot)
    {
        await using var storageLease = await StorageWriteLease.AcquireAsync(_blobStore, CancellationToken.None);
        var normalizedRoot = PathUtil.Normalize(newRoot);
        var items = await _items.GetByIdsAsync(itemIds, CancellationToken.None);

        foreach (var item in items)
        {
            // 根必须是源路径的**严格**祖先：根等于源路径的话，
            // 项自身的相对路径就是空串，写回去时会把这一层目录吃掉。
            if (string.Equals(normalizedRoot, item.SourcePath, StringComparison.OrdinalIgnoreCase)
                || !PathUtil.IsUnder(normalizedRoot, item.SourcePath))
            {
                throw new ArgumentException(
                    Strings.Format(Strings.NotAncestorRoot, normalizedRoot, item.SourcePath),
                    nameof(newRoot));
            }

            var oldRoot = item.RootPath;
            item.RootPath = normalizedRoot;
            item.RelativePath = PathUtil.GetRelative(normalizedRoot, item.SourcePath);

            var entries = await _entries.GetByItemAsync(item.Id, CancellationToken.None);

            foreach (var entry in entries)
            {
                // 条目的相对路径是相对 Item.RootPath 的，根一变就要重算
                entry.RelativePath = PathUtil.GetRelative(normalizedRoot, Path.Combine(oldRoot, entry.RelativePath));
            }

            // 先删旧的再写新的：顺序反了会把刚写进去的也一起删掉
            await _items.SaveSnapshotAsync(item, entries, create: false, CancellationToken.None);
        }
    }

    public async Task RemoveItemsAsync(IReadOnlyList<string> itemIds)
    {
        var removedGroup = false;
        await using (var lease = await StorageWriteLease.AcquireAsync(_blobStore, CancellationToken.None))
        {
            var items = await _items.GetByIdsAsync(itemIds, CancellationToken.None);
            await _items.DeleteManyAsync(itemIds, CancellationToken.None);
            foreach (var id in items.Select(item => item.GroupId).Distinct())
                removedGroup |= await _groups.DeleteEmptyTemporaryAsync(id, CancellationToken.None);
        }
        if (removedGroup) GroupsChanged?.Invoke();
    }

    public async Task MoveItemsAsync(IReadOnlyList<string> itemIds, string targetGroupId)
    {
        var removedGroup = false;
        await using (var lease = await StorageWriteLease.AcquireAsync(_blobStore, CancellationToken.None))
        {
            var items = await _items.GetByIdsAsync(itemIds, CancellationToken.None);
            var sources = items.Select(item => item.GroupId).Distinct().ToArray();
            foreach (var item in items)
            {
                item.GroupId = targetGroupId;
                await _items.UpdateAsync(item, CancellationToken.None);
            }
            foreach (var id in sources)
                removedGroup |= await _groups.DeleteEmptyTemporaryAsync(id, CancellationToken.None);
        }
        if (removedGroup) GroupsChanged?.Invoke();
    }

    public async Task<bool> DeleteEmptyTemporaryGroupAsync(string groupId)
    {
        bool removed;
        await using (var lease = await StorageWriteLease.AcquireAsync(_blobStore, CancellationToken.None))
            removed = await _groups.DeleteEmptyTemporaryAsync(groupId, CancellationToken.None);
        if (removed) GroupsChanged?.Invoke();
        return removed;
    }

    public async Task<int> DeleteEmptyTemporaryGroupsAsync(IReadOnlyCollection<string> excludedGroupIds, CancellationToken ct = default)
    {
        var removed = 0;
        await using (var lease = await StorageWriteLease.AcquireAsync(_blobStore, ct))
        {
            foreach (var group in await _groups.GetAllAsync(ct))
                if (group.Kind == StashGroupKind.Temporary && !excludedGroupIds.Contains(group.Id)
                    && await _groups.DeleteEmptyTemporaryAsync(group.Id, ct)) removed++;
        }
        if (removed > 0) GroupsChanged?.Invoke();
        return removed;
    }

    public async Task<IReadOnlyList<ItemSourceStatus>> CheckSourceStatusAsync(string groupId, CancellationToken ct)
    {
        var items = await _items.GetByGroupAsync(groupId, ct);
        var results = new List<ItemSourceStatus>();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            if (!File.Exists(item.SourcePath) && !Directory.Exists(item.SourcePath))
            {
                results.Add(new ItemSourceStatus(item.Id, item.DisplayName, ItemSourceState.Missing));
                continue;
            }

            var entries = await _entries.GetByItemAsync(item.Id, ct);
            var isDirectory = Directory.Exists(item.SourcePath);
            var modified = isDirectory != (item.Kind == StashItemKind.Folder);
            if (isDirectory && !modified)
            {
                var enumerated = await FolderEnumerator.EnumerateAsync(item.RootPath, item.SourcePath, _settings.Current.ExcludeGlobs, ct);
                if (enumerated.Warnings.Count > 0)
                    throw new IOException(string.Join(Environment.NewLine, enumerated.Warnings));
                var candidates = await FilterCandidatesAsync(enumerated.Entries, item.GitRepoRoot, ct);
                var currentPaths = candidates.Select(entry => entry.RelativePath + (entry.IsDirectory ? "\\" : string.Empty))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                modified = !currentPaths.SetEquals(entries.Select(entry =>
                    entry.RelativePath + (entry.IsDirectory ? "\\" : string.Empty)));
            }

            foreach (var entry in entries)
            {
                if (entry.IsDirectory || entry.Sha256 is null)
                {
                    continue;
                }

                var fullPath = Path.Combine(item.RootPath, entry.RelativePath);

                if (!File.Exists(fullPath))
                {
                    modified = true;
                    break;
                }

                if (!ZoneIdentifier.Matches(await ZoneIdentifier.ReadAsync(fullPath, ct), entry.ZoneIdentifier))
                {
                    modified = true;
                    break;
                }
                var info = new FileInfo(fullPath);

                if (info.Length != entry.Size)
                {
                    modified = true;
                    break;
                }

                var lastWrite = new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc));

                if (entry.SourceLastWriteUtc is null || lastWrite != entry.SourceLastWriteUtc.Value)
                {
                    // 大小一样但时间变了：才算哈希
                    var sha256 = await HashUtil.ComputeFileSha256Async(fullPath, ct);

                    if (!string.Equals(sha256, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        modified = true;
                        break;
                    }
                }
            }

            results.Add(new ItemSourceStatus(
                item.Id,
                item.DisplayName,
                modified ? ItemSourceState.Modified : ItemSourceState.Unchanged));
        }

        return results;
    }

    private async Task<IReadOnlyList<EnumeratedEntry>> FilterCandidatesAsync(
        IReadOnlyList<EnumeratedEntry> entries, string? repo, CancellationToken ct)
    {
        if (!_settings.Current.RespectGitIgnore || repo is null) return entries;
        var ignored = await _git.FilterIgnoredAsync(repo,
            entries.Where(entry => !entry.IsDirectory)
                .Select(entry => PathUtil.GetRelative(repo, entry.FullPath)).ToList(), ct);
        var ignoredPaths = ignored.Select(path => Path.GetFullPath(Path.Combine(repo, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return entries.Where(entry => entry.IsDirectory
            || !ignoredPaths.Contains(Path.GetFullPath(entry.FullPath))).ToList();
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static DateTime SafeLastWrite(string path)
    {
        try
        {
            return new FileInfo(path).LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }
}
