using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Utils;

namespace FileStash.Core.Services;

/// <summary>
/// 把暂存项物化成真实文件，放进 export\&lt;guid&gt;\。
/// </summary>
/// <remarks>
/// 拖出（S4）与"复制到剪贴板"共用这一套：先按 DragExport 规则算出每个条目的落点，
/// 再从 blob 把内容复制过去。
/// </remarks>
public sealed class DragExportService : IDragExportService
{
    private readonly IStashEntryRepository _entries;
    private readonly IBlobStore _blobStore;
    private readonly AppPaths _paths;
    private readonly PathMapper _mapper = new();

    public DragExportService(IStashEntryRepository entries, IBlobStore blobStore, AppPaths paths)
    {
        _entries = entries;
        _blobStore = blobStore;
        _paths = paths;
    }

    public async Task<IReadOnlyList<string>> MaterializeAsync(IReadOnlyList<StashItem> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var exportRoot = Path.Combine(_paths.ExportDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportRoot);

        var context = new PathMapper.MappingContext();
        var topLevel = new List<string>();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            var entries = await _entries.GetByItemAsync(item.Id, ct);
            var itemTopLevel = new List<string>();

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                var targetPath = _mapper.Map(item, entry, new TargetSpec(TargetMode.DragExport, null, StructureMode.ItemOnly), context, exportRoot);

                if (entry.IsDirectory || entry.Sha256 is null)
                {
                    // 空目录：建出来
                    Directory.CreateDirectory(targetPath);
                }
                else
                {
                    var directory = Path.GetDirectoryName(targetPath);

                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    await _blobStore.CopyBlobToAsync(entry.Sha256, targetPath, ct);
                }

                itemTopLevel.Add(targetPath);
            }

            // 顶层路径 = 这一项的落点里第一层（拖到桌面的是文件夹本身或文件本身）
            foreach (var path in itemTopLevel)
            {
                var relative = PathUtil.GetRelative(exportRoot, path);
                var firstSegment = PathUtil.SplitSegments(relative).FirstOrDefault();

                if (firstSegment is null)
                {
                    continue;
                }

                var top = Path.Combine(exportRoot, firstSegment);

                if (!topLevel.Contains(top, StringComparer.OrdinalIgnoreCase))
                {
                    topLevel.Add(top);
                }
            }
        }

        return topLevel;
    }
}
