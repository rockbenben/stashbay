using System.Text;
using System.Text.RegularExpressions;

namespace FileStash.Core.Utils;

/// <summary>
/// 通配符匹配。用于 excludeGlobs（如 "**/node_modules/**"）。
/// </summary>
/// <remarks>
/// 语义：`**` 匹配任意多个字符（含路径分隔符），`*` 匹配任意多个字符（不含分隔符），
/// `?` 匹配一个字符。比较不区分大小写。
/// </remarks>
public static partial class Glob
{
    public static bool IsMatch(string path, string pattern)
    {
        var regex = ToRegex(pattern);
        return regex.IsMatch(NormalizeSeparators(path));
    }

    public static bool IsMatchAny(string path, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (IsMatch(path, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>把通配符转成正则（带缓存，同一个 pattern 只编译一次）。</summary>
    public static Regex ToRegex(string pattern)
    {
        var key = NormalizeSeparators(pattern);
        return RegexCache.GetOrAdd(key, static value => new Regex(Translate(value), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private static string Translate(string pattern)
    {
        var builder = new StringBuilder("^");

        for (var i = 0; i < pattern.Length; i++)
        {
            var character = pattern[i];

            switch (character)
            {
                case '*' when i + 1 < pattern.Length && pattern[i + 1] == '*':
                    // "**/" → 可选的任意前缀（含零层目录）
                    if (i + 2 < pattern.Length && pattern[i + 2] == '\\')
                    {
                        builder.Append("(?:.*\\\\)?");
                        i += 2;
                    }
                    else
                    {
                        builder.Append(".*");
                        i += 1;
                    }

                    break;

                case '*':
                    builder.Append("[^\\\\]*");
                    break;

                case '?':
                    builder.Append("[^\\\\]");
                    break;

                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        builder.Append('$');
        return builder.ToString();
    }

    private static string NormalizeSeparators(string path) => path.Replace('/', '\\');

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> RegexCache = new();
}
