using System.Globalization;
using FileStash.Core.Resources;

namespace FileStash.Core.Utils;

/// <summary>
/// 路径工具。全部比较一律 OrdinalIgnoreCase，存储前一律 GetFullPath 规范化。
/// </summary>
public static class PathUtil
{
    /// <summary>
    /// 规范化：GetFullPath（含解析 ".."）+ 去掉末尾分隔符。
    /// </summary>
    /// <remarks>
    /// 盘符根（"C:\"）保留；UNC 共享根（"\\server\share"）的末尾分隔符由 GetFullPath 自己决定，
    /// 本方法不额外添加。
    /// </remarks>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(Strings.PathEmpty, nameof(path));
        }

        var full = Path.GetFullPath(path);
        return TrimTrailingSeparator(full);
    }

    /// <summary>
    /// 计算 full 相对 root 的相对路径。要求 full 位于 root 之下，否则抛异常。
    /// </summary>
    /// <remarks>返回的相对路径用 "\" 分隔，不以 "\" 开头。</remarks>
    public static string GetRelative(string root, string full)
    {
        var normalizedRoot = Normalize(root);
        var normalizedFull = Normalize(full);

        if (!IsUnder(normalizedRoot, normalizedFull))
        {
            throw new ArgumentException(
                Strings.Format(Strings.PathNotUnderRoot, normalizedFull, normalizedRoot),
                nameof(full));
        }

        if (string.Equals(normalizedRoot, normalizedFull, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var relative = Path.GetRelativePath(normalizedRoot, normalizedFull);
        return relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// candidate 是否等于 root 或位于 root 之下。用于防止路径越界（".." 攻击、剥离前缀后越界）。
    /// </summary>
    public static bool IsUnder(string root, string candidate)
    {
        var normalizedRoot = Normalize(root);
        var normalizedCandidate = Normalize(candidate);

        if (string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 注意必须带末尾分隔符再比较，否则 "C:\foo" 会把 "C:\foobar" 也算成在其之下
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 用于唯一性判断的键：不变式大写。
    /// </summary>
    /// <remarks>
    /// SQLite 的 NOCASE 只折叠 ASCII 字母，非 ASCII 路径（Ä / ä、Σ / σ）在它眼里是不同的，
    /// 重复项会漏判，所以唯一性用这个键，内存中的比较用 OrdinalIgnoreCase。
    /// 这个函数是幂等的：对已经归一化的键再跑一遍，结果不变 ——
    /// 所以仓储层可以无条件拿它去归一化"传进来的键"，不怕调用方传的是路径还是键。
    /// </remarks>
    public static string ToKey(string path) =>
        Normalize(path).ToUpperInvariant();

    /// <summary>去掉末尾分隔符，但盘符根（"C:\"）与 UNC 根（"\\server\share\"）保留。</summary>
    private static string TrimTrailingSeparator(string full)
    {
        var root = Path.GetPathRoot(full);

        if (root is not null && full.Length == root.Length)
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// 把绝对路径拆成"根 + 相对路径"。根取路径本身的父目录（子路径 = 文件/文件夹名）。
    /// </summary>
    /// <remarks>Git 仓库判定在 RootResolver（需要调 git），那是 M2 的事。</remarks>
    public static (string Root, string Relative) SplitAtParent(string path)
    {
        var normalized = Normalize(path);
        var root = Path.GetDirectoryName(normalized);

        if (string.IsNullOrEmpty(root))
        {
            // 盘符根：C:\foo 的父目录是 C:\
            root = Path.GetPathRoot(normalized) ?? normalized;
        }

        root = TrimTrailingSeparator(root);
        var relative = Path.GetFileName(normalized);
        return (root, relative);
    }

    /// <summary>把相对路径按 "\" 拆成段（去掉空段）。</summary>
    public static string[] SplitSegments(string relativePath) =>
        relativePath.Split(
            new[] { '\\', '/' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
