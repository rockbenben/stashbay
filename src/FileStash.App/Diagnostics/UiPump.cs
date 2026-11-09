using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace FileStash.App.Diagnostics;

/// <summary>
/// 自查 harness 的"等界面闲下来"。
/// </summary>
/// <remarks>
/// 窗口 Loaded 里挂的查询是异步的（源状态、历史、git status 都要跑一会儿）。
/// 只泵一次 Loaded 优先级，拍到的是"数据还没回来"那一帧 ——
/// 截图张张正常，条件性 UI 的那几行全是空，正好把要看的东西漏干净。
/// </remarks>
internal static class UiPump
{
    public static async Task Settle(Window window, int rounds = 15)
    {
        for (var round = 0; round < rounds; round++)
        {
            await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ContextIdle).Task;
            await Task.Delay(25);
        }

        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Loaded).Task;
        window.UpdateLayout();
    }

    /// <summary>按视觉树找元件（浮层与模板零件都在这棵上）。</summary>
    public static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }
}
