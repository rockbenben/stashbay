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

        await using var storageLease = await StorageWriteLease.AcquireAsync(_blobStore, ct);
        var exportRoot = Path.Combine(_paths.ExportDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportRoot);

        try
        {
            var names = new PathMapper.MappingContext();
            var topLevel = new List<string>();
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(item.SourcePath));
                var top = Path.Combine(exportRoot, names.ReserveFlattenName(name));
                var entries = await _entries.GetByItemAsync(item.Id, ct);
                if (item.Kind == StashItemKind.File
                    && (entries.Count != 1 || entries[0].IsDirectory || entries[0].Sha256 is null))
                    throw new IOException(item.SourcePath);
                if (item.Kind == StashItemKind.Folder)
                    Directory.CreateDirectory(top);

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var source = Path.Combine(item.RootPath, entry.RelativePath);
                    var inner = item.Kind == StashItemKind.File ? string.Empty
                        : PathUtil.GetRelative(item.SourcePath, source);
                    var targetPath = string.IsNullOrEmpty(inner) ? top : Path.Combine(top, inner);
                    if (!PathUtil.IsUnder(exportRoot, targetPath))
                        throw new IOException(targetPath);
                    if (entry.IsDirectory)
                        Directory.CreateDirectory(targetPath);
                    else if (entry.Sha256 is not null)
                    {
                        await _blobStore.CopyBlobToAsync(entry.Sha256, targetPath, ct);
                        await ZoneIdentifier.WriteAsync(targetPath, entry.ZoneIdentifier, ct);
                    }
                }
                if (entries.Count > 0 || item.Kind == StashItemKind.Folder)
                    topLevel.Add(top);
            }
            return topLevel;
        }
        catch
        {
            try { Directory.Delete(exportRoot, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}