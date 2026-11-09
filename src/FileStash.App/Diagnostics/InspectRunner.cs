using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FileStash.Core.Abstractions;
using FileStash.Core.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace FileStash.App.Diagnostics;

/// <summary>
/// `--inspect`：把暂存区的当前状态打印到标准输出。
/// </summary>
/// <remarks>
/// 给人看的诊断命令，也是验收脚本读库的入口 —— 脚本不该依赖仓库外的临时工程。
/// </remarks>
internal static class InspectRunner
{
    public static async Task<int> Run(IServiceProvider services)
    {
        // 自查模式：出错时不弹模态框（那会把进程吊住）
        App.InSmoke = true;

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // 没有控制台（从资源管理器启动）时忽略
        }

        try
        {
            var paths = services.GetRequiredService<AppPaths>();
            var groups = services.GetRequiredService<IStashGroupRepository>();
            var items = services.GetRequiredService<IStashItemRepository>();
            var entries = services.GetRequiredService<IStashEntryRepository>();
            var operations = services.GetRequiredService<IOperationRepository>();
            var operationEntries = services.GetRequiredService<IOperationEntryRepository>();

            Console.WriteLine($"数据目录\t{paths.RootDirectory}");
            Console.WriteLine($"数据库\t{paths.DatabasePath}");
            Console.WriteLine($"设置\t{paths.SettingsPath}");
            Console.WriteLine($"DPI感知\t{DescribeDpiContext()}");

            foreach (var group in await groups.GetAllAsync(CancellationToken.None))
            {
                var groupItems = await items.GetByGroupAsync(group.Id, CancellationToken.None);
                Console.WriteLine($"组\t{group.Name}\t{groupItems.Count} 项");

                foreach (var item in groupItems)
                {
                    var itemEntries = await entries.GetByItemAsync(item.Id, CancellationToken.None);

                    Console.WriteLine(
                        $"项\t{item.DisplayName}\t根={item.RootPath}\t子路径={item.RelativePath}" +
                        $"\t文件={item.FileCount}\t分支={item.GitBranch}\tId={item.Id}");

                    foreach (var entry in itemEntries)
                    {
                        Console.WriteLine(
                            $"条目\t{entry.RelativePath}\t{(entry.IsDirectory ? "目录" : entry.Size.ToString())}\t{entry.Sha256}");
                    }
                }
            }

            var history = await operations.GetAllAsync(CancellationToken.None);
            Console.WriteLine($"历史\t{history.Count} 条");

            foreach (var operation in history)
            {
                Console.WriteLine($"操作\t{operation.Id}\t{operation.Status}\t{operation.TargetDescription}");

                // 每一条的结果也要打出来：失败的原因（"被 EXCEL.EXE 占用""无权限"…）只存在这里，
                // 只看操作状态是看不出"为什么没写进去"的
                var written = await operationEntries.GetByOperationAsync(operation.Id, CancellationToken.None);

                foreach (var entry in written)
                {
                    Console.WriteLine(
                        $"写入\t{entry.TargetPath}\t{entry.Action}" +
                        $"{(entry.Error is null ? string.Empty : "：" + entry.Error)}");
                }
            }

            File.WriteAllText(App.SmokeReportPath, "OK");
            return 0;
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "inspect 失败");
            File.WriteAllText(App.SmokeReportPath, ex.ToString());
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    /// <summary>
    /// 报当前的 DPI 感知上下文。
    /// </summary>
    /// <remarks>
    /// 用 AreDpiAwarenessContextsEqual 去比 PerMonitorV2（值 -4）；
    /// **不要**用 GetAwarenessFromDpiAwarenessContext —— 那个 API 压根没有 V2 这一档，
    /// 最大值就是 2，配好了也查不出来。
    /// </remarks>
    private static string DescribeDpiContext()
    {
        try
        {
            const int perMonitorAwareV2 = -4;
            var context = NativeMethods.GetThreadDpiAwarenessContext();

            if (NativeMethods.AreDpiAwarenessContextsEqual(context, perMonitorAwareV2))
            {
                return "PerMonitorV2";
            }

            return context.ToString();
        }
        catch (DllNotFoundException)
        {
            return "未知";
        }
        catch (EntryPointNotFoundException)
        {
            return "未知";
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern IntPtr GetThreadDpiAwarenessContext();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AreDpiAwarenessContextsEqual(IntPtr value1, IntPtr value2);
    }
}
