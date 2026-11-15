using System.Runtime.InteropServices;
using FileStash.Core.Abstractions;

namespace FileStash.Infrastructure.Native;

/// <summary>
/// 原子替换文件。
/// </summary>
/// <remarks>
/// 用 <c>ReplaceFileW</c> 而不是 <c>File.Move(..., overwrite: true)</c>：
/// 后者是"用临时文件的记录顶掉目标的记录"，目标的属性、ACL、创建时间全丢；
/// 前者保留目标原有的这些信息，而且同卷下是原子替换。
/// 目标不存在时（新建）没有可保留的东西，直接移动。
/// </remarks>
public sealed class AtomicFileWriter : IAtomicFileWriter
{
    public Task<bool> CreateAsync(string temporaryPath, string targetPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        File.Move(temporaryPath, targetPath, overwrite: false);
        return Task.FromResult(true);
    }

    public Task<bool> ReplaceAsync(string temporaryPath, string targetPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!File.Exists(targetPath))
        {
            File.Move(temporaryPath, targetPath);
            return Task.FromResult(true);
        }

        var replaced = ReplaceFile(targetPath, temporaryPath, null, 0, IntPtr.Zero, IntPtr.Zero);

        if (!replaced)
        {
            // 回退策略：网络盘或不支持 ReplaceFile 的文件系统上，退回到常规覆盖移动
            try
            {
                File.Move(temporaryPath, targetPath, overwrite: true);
                replaced = true;
            }
            catch
            {
                // 保留 false，由上层抛出并排查锁定原因
            }
        }

        return Task.FromResult(replaced);
    }

    public IDisposable GuardTarget(string path)
    {
        var handles = new List<Microsoft.Win32.SafeHandles.SafeFileHandle>();
        try
        {
            var ancestors = new Stack<string>();
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            while (!string.IsNullOrEmpty(directory))
            {
                if (Directory.Exists(directory)) ancestors.Push(directory);
                directory = Path.GetDirectoryName(directory);
            }
            foreach (var ancestor in ancestors)
            {
                var handle = CreateFile(ancestor, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    throw new IOException(ancestor, Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                }
                handles.Add(handle);
                if (new DirectoryInfo(ancestor).LinkTarget is not null)
                    throw new IOException(ancestor);
            }
            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null)
                throw new IOException(path);
            if (File.Exists(path))
            {
                if (new FileInfo(path).LinkTarget is not null) throw new IOException(path);
                handles.Add(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete));
            }
            return new TargetGuard(handles);
        }
        catch
        {
            foreach (var handle in handles) handle.Dispose();
            throw;
        }
    }

    private sealed class TargetGuard(List<Microsoft.Win32.SafeHandles.SafeFileHandle> handles) : IDisposable
    {
        public void Dispose()
        {
            foreach (var handle in handles) handle.Dispose();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    public string? GetIdentity(string path)
    {
        try
        {
            using var handle = CreateFile(path, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            return GetIdentity(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? GetIdentity(Microsoft.Win32.SafeHandles.SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var info)
            ? $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}" : null;

    public bool DeleteDirectory(string path, string expectedIdentity)
    {
        using var guard = GuardTarget(path);
        // DELETE + FILE_READ_ATTRIBUTES, with no delete sharing, prevents replacement
        // between identity validation and deletion. OPEN_REPARSE_POINT avoids following links.
        using var handle = CreateFile(path, 0x10080, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException(path, Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        if (GetIdentity(handle) != expectedIdentity) return false;
        var disposition = 1;
        if (!SetFileInformationByHandle(handle, 4, ref disposition, sizeof(int)))
            throw new IOException(path, Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle, int informationClass, ref int information, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplaceFile(
        string replacedFileName,
        string replacementFileName,
        string? backupFileName,
        uint replaceFlags,
        IntPtr exclude,
        IntPtr reserved);
}
