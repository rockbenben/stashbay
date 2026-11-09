using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileStash.App.Services;

/// <summary>
/// 提取 Windows 系统关联的原生文件与文件夹图标。
/// 使用 SHGFI_USEFILEATTRIBUTES 基于扩展名直接读取系统图标缓存，无需发生实际磁盘 I/O。
/// </summary>
public static class ShellIconService
{
    private static readonly ConcurrentDictionary<string, ImageSource> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiUseFileAttributes = 0x000000010;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 获取指定路径或扩展名对应的系统真实小图标（16x16 / 32x32 DPI 自适应）。
    /// </summary>
    public static ImageSource? GetIcon(string path, bool isFolder)
    {
        var cacheKey = isFolder ? ":folder:" : Path.GetExtension(path);
        if (string.IsNullOrEmpty(cacheKey))
        {
            cacheKey = ":generic_file:";
        }

        if (IconCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            var icon = ExtractShellIcon(path, isFolder);
            if (icon is not null)
            {
                IconCache[cacheKey] = icon;
                return icon;
            }
        }
        catch
        {
            // 兜底返回 null，界面回退到矢量图标
        }

        return null;
    }

    private static ImageSource? ExtractShellIcon(string path, bool isFolder)
    {
        var shinfo = new SHFILEINFO();
        var flags = ShgfiIcon | ShgfiSmallIcon | ShgfiUseFileAttributes;
        var attributes = isFolder ? FileAttributeDirectory : FileAttributeNormal;

        // 如果是真实存在的 exe / lnk / 快捷方式，优先提取其实际独立图标；否则按扩展名通用属性提取
        var targetPath = path;
        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            flags = ShgfiIcon | ShgfiSmallIcon;
            attributes = 0;
        }
        else if (string.IsNullOrEmpty(targetPath))
        {
            targetPath = isFolder ? "folder" : "file.txt";
        }

        var res = SHGetFileInfo(targetPath, attributes, ref shinfo, (uint)Marshal.SizeOf(shinfo), flags);
        if (res == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(
                shinfo.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }
}
