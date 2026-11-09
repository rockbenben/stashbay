namespace FileStash.App.Startup;

/// <summary>解析出来的命令行。</summary>
/// <param name="AddPaths">--add 后面跟的路径。</param>
/// <param name="ApplyHerePath">--apply-here 后面跟的目录。</param>
/// <param name="ApplyOriginal">--apply-original。</param>
/// <param name="GroupName">--group 后面跟的组名。</param>
/// <param name="Minimized">--minimized（开机自启）。</param>
/// <param name="ElevatedRetryOperationId">--elevated-retry 后面跟的操作 ID。</param>
/// <param name="Show">--show（重启自己时用，忽略"启动时最小化"）。</param>
/// <param name="UnregisterShell">--unregister-shell（卸载时清掉右键菜单与自启）。</param>
/// <param name="Quit">--quit（让正在运行的那个实例退出）。</param>
public sealed record CommandLine(
    IReadOnlyList<string> AddPaths,
    string? ApplyHerePath,
    bool ApplyOriginal,
    string? GroupName,
    bool Minimized,
    string? ElevatedRetryOperationId,
    bool Show,
    bool UnregisterShell,
    bool Quit)
{
    public bool IsEmpty => AddPaths.Count == 0
        && ApplyHerePath is null
        && !ApplyOriginal
        && !Minimized
        && ElevatedRetryOperationId is null
        && !UnregisterShell
        && !Quit;

    /// <summary>解析命令行。</summary>
    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        var addPaths = new List<string>();
        string? applyHere = null;
        string? group = null;
        string? elevatedRetry = null;
        var applyOriginal = false;
        var minimized = false;
        var show = false;
        var unregisterShell = false;
        var quit = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];

            switch (argument)
            {
                case "--add":
                    // 后面所有非选项参数都是路径
                    while (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        addPaths.Add(args[++index]);
                    }

                    break;

                case "--apply-here":
                    if (index + 1 < args.Count)
                    {
                        applyHere = args[++index];
                    }

                    break;

                case "--apply-original":
                    applyOriginal = true;
                    break;

                case "--group":
                    if (index + 1 < args.Count)
                    {
                        group = args[++index];
                    }

                    break;

                case "--minimized":
                    minimized = true;
                    break;

                case "--show":
                    show = true;
                    break;

                case "--unregister-shell":
                    unregisterShell = true;
                    break;

                case "--quit":
                    quit = true;
                    break;

                case "--elevated-retry":
                    if (index + 1 < args.Count)
                    {
                        elevatedRetry = args[++index];
                    }

                    break;
            }
        }

        return new CommandLine(addPaths, applyHere, applyOriginal, group, minimized, elevatedRetry, show, unregisterShell, quit);
    }
}
