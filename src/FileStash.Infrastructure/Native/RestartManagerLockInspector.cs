using System.Diagnostics;
using System.Runtime.InteropServices;
using FileStash.Core.Abstractions;
using FileStash.Core.Resources;

namespace FileStash.Infrastructure.Native;

/// <summary>
/// 用 Restart Manager 查出是谁占着文件。
/// </summary>
/// <remarks>
/// 比"打开文件试试能不能"要准：文件被 Excel / Word 独占时，
/// 我们要报出进程名（"被 EXCEL.EXE 占用"），而不是一句"IOException"。
/// </remarks>
public sealed class RestartManagerLockInspector : IFileLockInspector
{
    private const int ErrorMoreData = 234;

    public Task<IReadOnlyList<string>> GetLockingProcessesAsync(string path, CancellationToken ct)
    {
        var names = new List<string>();

        if (!File.Exists(path))
        {
            return Task.FromResult<IReadOnlyList<string>>(names);
        }

        uint sessionHandle;

        if (RmStartSession(out sessionHandle, 0, Guid.NewGuid().ToString()) != 0)
        {
            return Task.FromResult<IReadOnlyList<string>>(names);
        }

        try
        {
            string[] resources = [path];

            if (RmRegisterResources(sessionHandle, (uint)resources.Length, resources, 0, null, 0, null) != 0)
            {
                return Task.FromResult<IReadOnlyList<string>>(names);
            }

            uint needed = 0;
            uint count = 0;
            uint reasons = 0;

            var result = RmGetList(sessionHandle, out needed, ref count, null, ref reasons);

            if (result != ErrorMoreData && result != 0)
            {
                return Task.FromResult<IReadOnlyList<string>>(names);
            }

            if (needed == 0)
            {
                return Task.FromResult<IReadOnlyList<string>>(names);
            }

            var processInfos = new RmProcessInfo[needed];
            count = needed;

            if (RmGetList(sessionHandle, out needed, ref count, processInfos, ref reasons) != 0)
            {
                return Task.FromResult<IReadOnlyList<string>>(names);
            }

            for (var index = 0; index < count; index++)
            {
                names.Add(ResolveProcessName(processInfos[index]));
            }
        }
        finally
        {
            RmEndSession(sessionHandle);
        }

        return Task.FromResult<IReadOnlyList<string>>(names);
    }

    private static string ResolveProcessName(RmProcessInfo info)
    {
        try
        {
            using var process = Process.GetProcessById(info.Process.ProcessId);
            return process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // 进程已经退出，或者没权限读它的名字
            return string.IsNullOrEmpty(info.ApplicationName) ? Strings.UnknownProcess : info.ApplicationName;
        }
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int sessionFlags, string sessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        string[] fileNames,
        uint applicationCount,
        RmUniqueProcess[]? applications,
        uint serviceCount,
        string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint processInfoNeeded,
        ref uint processInfo,
        [In, Out] RmProcessInfo[]? affectedApps,
        ref uint rebootReasons);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;

        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ApplicationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TerminalSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }
}
