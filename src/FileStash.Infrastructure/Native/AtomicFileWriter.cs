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
