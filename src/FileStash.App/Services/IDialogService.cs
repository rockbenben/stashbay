namespace FileStash.App.Services;

/// <summary>界面交互的抽象。ViewModel 不直接碰 WPF 窗口，便于测试。</summary>
public interface IDialogService
{
    /// <summary>混选要暂存的文件和文件夹；取消返回 null。</summary>
    IReadOnlyList<string>? PickStashPaths();

    /// <summary>选文件夹；用户取消返回 null。</summary>
    string? PickFolder();

    /// <summary>单行文本输入（重命名等）；取消返回 null。</summary>
    string? PromptText(string title, string initialValue);

    /// <summary>是 / 否确认。</summary>
    bool Confirm(string title, string message);

    /// <summary>从源路径的各级祖先里选一个作为根；取消返回 null。</summary>
    string? PickSetRoot(string sourcePath);

    /// <summary>从给定的组里选一个（移动到组）；取消返回 null。</summary>
    string? PickGroup(IReadOnlyList<(string Id, string Name)> groups, string currentGroupId);

    /// <summary>剪贴板里的文件路径；没有返回 null。</summary>
    IReadOnlyList<string>? GetClipboardPaths();

    /// <summary>把文件路径放进剪贴板（资源管理器里可以 Ctrl+V 粘贴）。</summary>
    void SetClipboardPaths(IReadOnlyList<string> paths);

    /// <summary>操作结果等纯告知信息，以自动消失的非模态提示展示。</summary>
    void ShowInfo(string title, string message);

    void ShowWarning(string title, string message);

    /// <summary>
    /// 飘一个非模态提示（回执）。
    /// </summary>
    /// <remarks>
    /// 回执不该用模态框：模态框会打断用户，还会占住主线程的模态循环
    /// （后台会话里更是直接把进程吊住）。
    /// </remarks>
    void ShowToast(string title, string summary);
}
