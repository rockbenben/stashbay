using System.Globalization;
using System.Resources;

namespace FileStash.Core.Resources;

/// <summary>
/// 界面文字。View 里用 <c>x:Static</c> 引用，不写死字面量。
/// </summary>
/// <remarks>
/// 中性资源（Strings.resx）放中文原文，其余每门语言一张 <c>Strings.&lt;文化&gt;.resx</c>
/// 卫星程序集（en / ja-JP / de-DE / ar-SA …，共 18 门）。
/// 没有 zh-CN 卫星：中性资源本来就是中文，所有 zh-* 区域设置都会回退到它，
/// 再放一份字节相同的中文表只会带来漂移。
/// </remarks>
public static class Strings
{
    private static readonly ResourceManager ResourceManager =
        new("FileStash.Core.Resources.Strings", typeof(Strings).Assembly);
    public static string BackupExpired => Get(nameof(BackupExpired));
    public static string BlobNotFound => Get(nameof(BlobNotFound));
    public static string BranchChangedText => Get(nameof(BranchChangedText));
    public static string Cancelled => Get(nameof(Cancelled));
    public static string DeleteDirFailed => Get(nameof(DeleteDirFailed));
    public static string DeleteFileFailed => Get(nameof(DeleteFileFailed));
    public static string DirNotEmpty => Get(nameof(DirNotEmpty));
    public static string DirUnreadableWarning => Get(nameof(DirUnreadableWarning));
    public static string DuplicateTargetWarning => Get(nameof(DuplicateTargetWarning));
    public static string FileModifiedNotDeleted => Get(nameof(FileModifiedNotDeleted));
    public static string FileModifiedNotRestored => Get(nameof(FileModifiedNotRestored));
    public static string FileUnreadableWarning => Get(nameof(FileUnreadableWarning));
    public static string GitStatusAdded => Get(nameof(GitStatusAdded));
    public static string GitStatusCopied => Get(nameof(GitStatusCopied));
    public static string GitStatusDeleted => Get(nameof(GitStatusDeleted));
    public static string GitStatusModified => Get(nameof(GitStatusModified));
    public static string GitStatusRenamed => Get(nameof(GitStatusRenamed));
    public static string GitStatusUntracked => Get(nameof(GitStatusUntracked));
    public static string InvalidSha256 => Get(nameof(InvalidSha256));
    public static string JunctionSkippedWarning => Get(nameof(JunctionSkippedWarning));
    public static string ListSeparator => Get(nameof(ListSeparator));
    public static string LockedByProcess => Get(nameof(LockedByProcess));
    public static string NewGroupName => Get(nameof(NewGroupName));
    public static string NoBackup => Get(nameof(NoBackup));
    public static string NoParentDirectory => Get(nameof(NoParentDirectory));
    public static string NoPermission => Get(nameof(NoPermission));
    public static string NotAncestorRoot => Get(nameof(NotAncestorRoot));
    public static string OperationNotFound => Get(nameof(OperationNotFound));
    public static string PathEmpty => Get(nameof(PathEmpty));
    public static string PathNotFound => Get(nameof(PathNotFound));
    public static string PathNotUnderRoot => Get(nameof(PathNotUnderRoot));
    public static string PathWithError => Get(nameof(PathWithError));
    public static string RenameTargetWarning => Get(nameof(RenameTargetWarning));
    public static string RestoreFailed => Get(nameof(RestoreFailed));
    public static string ShellLegacyShortcutName => Get(nameof(ShellLegacyShortcutName));
    public static string ShellMenuAddToStash => Get(nameof(ShellMenuAddToStash));
    public static string ShellMenuApplyHere => Get(nameof(ShellMenuApplyHere));
    public static string ShellMenuApplyToFolder => Get(nameof(ShellMenuApplyToFolder));
    public static string ShellShortcutDescription => Get(nameof(ShellShortcutDescription));
    public static string ShellShortcutName => Get(nameof(ShellShortcutName));
    public static string StillOnSourceBranchText => Get(nameof(StillOnSourceBranchText));
    public static string SymlinkSkippedWarning => Get(nameof(SymlinkSkippedWarning));
    public static string TargetDragExport => Get(nameof(TargetDragExport));
    public static string TargetIsFileNameWarning => Get(nameof(TargetIsFileNameWarning));
    public static string TargetMissing => Get(nameof(TargetMissing));
    public static string TargetOriginalLocation => Get(nameof(TargetOriginalLocation));
    public static string TargetReadOnly => Get(nameof(TargetReadOnly));
    public static string UncommittedChangesWarning => Get(nameof(UncommittedChangesWarning));
    public static string UnknownProcess => Get(nameof(UnknownProcess));
    public static string WriteFailedDetail => Get(nameof(WriteFailedDetail));
    /// <summary>带占位符的文案：按当前界面语言格式化。</summary>
    public static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, format, args);

    private static string Get(string key) =>
        ResourceManager.GetString(key) ?? key;
}