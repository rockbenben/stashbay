using FileStash.Core.Resources;
using FileStash.Core.Utils;

namespace FileStash.Core.Models;

/// <summary>枚举出来的一个文件或目录。</summary>
/// <param name="RelativePath">相对根目录的路径（用 "\" 分隔）。</param>
/// <param name="FullPath">绝对路径。</param>
/// <param name="IsDirectory">是否为空目录（目录本身不存内容，只在为空时记录）。</param>
/// <param name="Size">文件字节数。</param>
/// <param name="LastWriteTimeUtc">最后写入时间。</param>
public sealed record EnumeratedEntry(
    string RelativePath,
    string FullPath,
    bool IsDirectory,
    long Size,
    DateTime LastWriteTimeUtc);

/// <summary>枚举结果。</summary>
public sealed record EnumerationResult(IReadOnlyList<EnumeratedEntry> Entries, IReadOnlyList<string> Warnings);

/// <summary>
/// 文件夹递归枚举。
/// </summary>
/// <remarks>
/// 三条规矩，每条都有代价：
/// ① 不用 <c>Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories)</c>：
///    那个重载走兼容选项（IgnoreInaccessible=false），一个读不了的子目录就掀掉整棵遍历，
///    外面的 catch 又常按"根目录"写，于是"有一个子目录读不了"被升级成"这台机器没有这个目录"。
/// ② 不用 AttributesToSkip=ReparsePoint 一刀切：那会把 OneDrive 等云同步的占位文件一起排除
///    （云文件本身就是重解析点）。这里改成逐个判断：真正的符号链接/联接跳过，
///    云占位文件当普通文件处理（读取会触发下载）。
/// ③ catch 的粒度落在单个条目上，不落在整棵树上。
/// </remarks>
public static class FolderEnumerator
{
    public static EnumerationResult Enumerate(string root, string folderPath, IReadOnlyList<string>? excludeGlobs = null)
    {
        var entries = new List<EnumeratedEntry>();
        var warnings = new List<string>();
        var visitedDirectories = new List<string>();

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,   // 隐藏 / 系统文件也要暂存
        };

        var stack = new Stack<string>();
        stack.Push(folderPath);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            string[] files;
            string[] directories;

            try
            {
                files = Directory.GetFiles(current, "*", options);
                directories = Directory.GetDirectories(current, "*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add(Strings.Format(Strings.DirUnreadableWarning, current, ex.Message));
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);

                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // LinkTarget 非空 = 真正的链接（符号链接 / 挂载点）；空 = 云占位之类的其它重解析点
                        if (info.LinkTarget is not null)
                        {
                            warnings.Add(Strings.Format(Strings.SymlinkSkippedWarning, file));
                            continue;
                        }
                    }

                    var relative = PathUtil.GetRelative(root, file);

                    // .git 可能是文件（worktree 的指针），也不能进快照
                    if (string.Equals(Path.GetFileName(file), ".git", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (excludeGlobs is { Count: > 0 } && Glob.IsMatchAny(relative, excludeGlobs))
                    {
                        continue;
                    }

                    entries.Add(new EnumeratedEntry(relative, file, false, info.Length, info.LastWriteTimeUtc));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add(Strings.Format(Strings.FileUnreadableWarning, file, ex.Message));
                }
            }

            foreach (var directory in directories)
            {
                try
                {
                    var info = new DirectoryInfo(directory);

                    // **跳过 .git**：把整个仓库文件夹暂存起来时，
                    // 不跳的话会把 .git 里的全部内部文件（几百上千个）一起快照进去 ——
                    // 又大又没意义，而且每次提交都会变。
                    if (string.Equals(info.Name, ".git", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null)
                    {
                        warnings.Add(Strings.Format(Strings.JunctionSkippedWarning, directory));
                        continue;
                    }

                    var relative = PathUtil.GetRelative(root, directory);

                    if (excludeGlobs is { Count: > 0 } && Glob.IsMatchAny(relative, excludeGlobs))
                    {
                        continue;
                    }

                    visitedDirectories.Add(relative);
                    stack.Push(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add(Strings.Format(Strings.DirUnreadableWarning, directory, ex.Message));
                }
            }
        }

        // 空目录：下面一个条目都没有的目录，记成一条目录项（应用时会创建出来）
        // 按深度从深到浅处理：只含空子目录的父目录，要等子目录先成为条目，它才算"有内容"
        foreach (var directory in visitedDirectories
                     .OrderByDescending(path => path.Count(character => character == '\\')))
        {
            var prefix = directory + "\\";
            var hasContent = entries.Any(entry => entry.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            if (!hasContent)
            {
                entries.Add(new EnumeratedEntry(directory, Path.Combine(root, directory), true, 0, DateTime.MinValue));
            }
        }

        return new EnumerationResult(entries, warnings);
    }
}
