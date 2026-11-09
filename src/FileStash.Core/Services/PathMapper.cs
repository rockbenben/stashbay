using FileStash.Core.Models;
using FileStash.Core.Utils;

namespace FileStash.Core.Services;

/// <summary>
/// 路径映射（纯函数，重点单测）。
/// </summary>
public sealed class PathMapper
{
    /// <summary>
    /// 一次计划内的映射上下文：平铺模式下的重名登记表。
    /// </summary>
    public sealed class MappingContext
    {
        private readonly HashSet<string> _usedNames = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>计划内是否只有一个文件（决定能不能"目标即文件名"）。</summary>
        public bool SingleFile { get; init; }

        /// <summary>
        /// 登记一个平铺后的文件名：重名时自动加 " (2)"、" (3)"。
        /// </summary>
        public string ReserveFlattenName(string fileName)
        {
            if (_usedNames.Add(fileName))
            {
                return fileName;
            }

            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);

            for (var index = 2; ; index++)
            {
                var candidate = $"{baseName} ({index}){extension}";

                if (_usedNames.Add(candidate))
                {
                    return candidate;
                }
            }
        }
    }

    /// <summary>
    /// 计算一个条目的目标绝对路径。
    /// </summary>
    /// <param name="item">暂存项。</param>
    /// <param name="entry">条目。</param>
    /// <param name="target">目标描述。</param>
    /// <param name="context">映射上下文。</param>
    /// <param name="exportRoot">DragExport 模式下的物化根目录。</param>
    public string Map(
        StashItem item,
        StashEntry entry,
        TargetSpec target,
        MappingContext context,
        string? exportRoot = null)
    {
        var targetPath = ExpandTargetPath(target.TargetPath);

        return target.Mode switch
        {
            TargetMode.OriginalLocation => MapOriginal(item, entry, target),
            TargetMode.AnotherRoot => MapAnotherRoot(item, entry, target, targetPath),
            TargetMode.SpecifiedPath => MapSpecified(item, entry, target, targetPath, context),
            TargetMode.DragExport => MapDragExport(item, entry, exportRoot ?? string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
    }

    /// <summary>S1：原位置。基准根是 Item.RootPath（唯一基准，GitRepoRoot 只是元数据）。</summary>
    private static string MapOriginal(StashItem item, StashEntry entry, TargetSpec target)
    {
        var relative = entry.RelativePath;

        if (target.Structure == StructureMode.ItemOnly)
        {
            relative = ItemOnlyRelative(item, entry);
        }
        else if (target.Structure == StructureMode.Flatten)
        {
            relative = Path.GetFileName(entry.RelativePath);
        }

        return Path.Combine(item.RootPath, relative);
    }

    /// <summary>S2：其他根目录。</summary>
    private static string MapAnotherRoot(StashItem item, StashEntry entry, TargetSpec target, string targetRoot)
    {
        var relative = target.Structure switch
        {
            StructureMode.ItemOnly => ItemOnlyRelative(item, entry),
            StructureMode.Flatten => Path.GetFileName(entry.RelativePath),
            _ => entry.RelativePath,
        };

        if (target.Structure == StructureMode.FullRelative && target.StripLeadingSegments > 0)
        {
            var segments = PathUtil.SplitSegments(relative);
            relative = string.Join('\\', segments.Skip(target.StripLeadingSegments));
        }

        return Path.Combine(targetRoot, relative);
    }

    /// <summary>S3：指定路径。</summary>
    private static string MapSpecified(
        StashItem item,
        StashEntry entry,
        TargetSpec target,
        string targetPath,
        MappingContext context)
    {
        // 目标即完整文件名：计划内只有一个文件时才允许
        if (target.TreatTargetAsFileName && context.SingleFile)
        {
            return targetPath;
        }

        var relative = target.Structure switch
        {
            StructureMode.ItemOnly => ItemOnlyRelative(item, entry),
            StructureMode.Flatten => context.ReserveFlattenName(Path.GetFileName(entry.RelativePath)),
            _ => entry.RelativePath,
        };

        return Path.Combine(targetPath, relative);
    }

    /// <summary>S4：拖出。物化到 export 目录，按 ItemOnly 规则。</summary>
    private static string MapDragExport(StashItem item, StashEntry entry, string exportRoot)
    {
        var relative = ItemOnlyRelative(item, entry);
        return Path.Combine(exportRoot, relative);
    }

    /// <summary>
    /// ItemOnly：保留项自身名称与内部结构，砍掉项之上的祖先段。
    /// </summary>
    private static string ItemOnlyRelative(StashItem item, StashEntry entry)
    {
        var inner = entry.RelativePath;

        var prefix = item.RelativePath;

        if (!string.IsNullOrEmpty(prefix)
            && inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            inner = inner[prefix.Length..].TrimStart('\\');
        }

        var ownName = string.IsNullOrEmpty(prefix)
            ? Path.GetFileName(entry.RelativePath)
            : Path.GetFileName(prefix);

        return string.IsNullOrEmpty(inner) ? ownName : Path.Combine(ownName, inner);
    }

    /// <summary>目标路径支持环境变量，两端引号自动去掉。</summary>
    public static string ExpandTargetPath(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return string.Empty;
        }

        var expanded = Environment.ExpandEnvironmentVariables(targetPath.Trim());

        return expanded.Trim().Trim('"');
    }
}
