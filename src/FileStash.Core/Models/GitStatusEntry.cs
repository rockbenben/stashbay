using FileStash.Core.Resources;

namespace FileStash.Core.Models;

/// <summary>git status 里的一个条目。</summary>
/// <param name="Status">
/// 两位状态码：M 修改、A 新增、D 删除、R 重命名、C 复制、? 未跟踪、! 忽略。
/// </param>
/// <param name="Path">相对仓库根的路径。</param>
/// <param name="OriginalPath">重命名 / 复制时的原路径。</param>
public sealed record GitStatusEntry(string Status, string Path, string? OriginalPath = null)
{
    /// <summary>已删除的文件不能加入暂存（源路径已经不存在了）。</summary>
    public bool IsDeleted => Status.Contains('D', StringComparison.Ordinal);

    public bool IsUntracked => Status == "??";

    public bool IsModified => Status.Contains('M', StringComparison.Ordinal);

    /// <summary>状态的中文说明。</summary>
    public string Describe() => Status switch
    {
        "??" => Strings.GitStatusUntracked,
        "M " or " M" => Strings.GitStatusModified,
        "A " or " A" => Strings.GitStatusAdded,
        "D " or " D" => Strings.GitStatusDeleted,
        "R " or " R" => Strings.GitStatusRenamed,
        "C " or " C" => Strings.GitStatusCopied,
        _ => Status.Trim(),
    };
}
