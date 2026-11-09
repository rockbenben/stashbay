using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace FileStash.App.Services;

/// <summary>
/// 拖拽与剪贴板有效载荷提取助手：
/// 支持文件列表（FileDrop）、纯文本或 URL（UnicodeText）、图像位图（Bitmap）、互联网快捷方式（UniformResourceLocator）。
/// </summary>
public static class DataPayloadHelper
{
    /// <summary>检查拖拽数据是否包含支持的格式。</summary>
    public static bool IsSupported(IDataObject? data)
    {
        if (data is null)
        {
            return false;
        }

        try
        {
            return data.GetDataPresent(DataFormats.FileDrop)
                || data.GetDataPresent(DataFormats.UnicodeText)
                || data.GetDataPresent(DataFormats.Text)
                || data.GetDataPresent(DataFormats.Bitmap)
                || data.GetDataPresent("UniformResourceLocator")
                || data.GetDataPresent("UniformResourceLocatorW");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从拖拽或剪贴板载荷中提取文件路径列表：
    /// 若为已有文件则直接返回；
    /// 若为文本/URL/图片，则在 targetDirectory 落地生成物理文件并返回其路径。
    /// </summary>
    public static IReadOnlyList<string> ExtractPayloadPaths(IDataObject? data, string targetDirectory)
    {
        if (data is null)
        {
            return [];
        }

        try
        {
            // 1. 文件拖拽
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                if (data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    return files.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
                }
            }

            // 2. URL 快捷方式拖拽（例如从浏览器地址栏图标或网页链接拖出）
            if (data.GetDataPresent("UniformResourceLocatorW") || data.GetDataPresent("UniformResourceLocator"))
            {
                var urlObj = data.GetData("UniformResourceLocatorW") ?? data.GetData("UniformResourceLocator");
                string? url = null;
                if (urlObj is string s)
                {
                    url = s;
                }
                else if (urlObj is MemoryStream ms)
                {
                    using var reader = new StreamReader(ms, System.Text.Encoding.Default);
                    url = reader.ReadToEnd().TrimEnd('\0');
                }

                if (!string.IsNullOrWhiteSpace(url))
                {
                    var saved = SaveTextSnippetOrLink(url, targetDirectory);
                    if (!string.IsNullOrEmpty(saved))
                    {
                        return [saved];
                    }
                }
            }

            // 3. 图像/位图拖拽
            if (data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent(typeof(BitmapSource)))
            {
                var bmpSource = data.GetData(typeof(BitmapSource)) as BitmapSource;
                if (bmpSource is null && data.GetData(DataFormats.Bitmap) is System.Drawing.Bitmap gdiBmp)
                {
                    using var stream = new MemoryStream();
                    gdiBmp.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    stream.Position = 0;
                    var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    bmpSource = decoder.Frames[0];
                }

                if (bmpSource is not null)
                {
                    var saved = SaveImageSnippet(bmpSource, targetDirectory);
                    if (!string.IsNullOrEmpty(saved))
                    {
                        return [saved];
                    }
                }
            }

            // 4. 纯文本拖拽（选中文本拖入窗口）
            if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
            {
                var text = (data.GetData(DataFormats.UnicodeText) as string)
                    ?? (data.GetData(DataFormats.Text) as string);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    var saved = SaveTextSnippetOrLink(text, targetDirectory);
                    if (!string.IsNullOrEmpty(saved))
                    {
                        return [saved];
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "提取拖放数据载荷异常");
        }

        return [];
    }

    /// <summary>把纯文本或网址存为 .txt 或 .url 文件。</summary>
    public static string? SaveTextSnippetOrLink(string text, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var unique = Guid.NewGuid().ToString("N")[..4];
            var trimmed = text.Trim();

            var isUrl = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

            if (isUrl)
            {
                var filePath = Path.Combine(targetDirectory, $"Link_{stamp}_{unique}.url");
                File.WriteAllText(filePath, $"[InternetShortcut]\r\nURL={trimmed}\r\n", System.Text.Encoding.UTF8);
                return filePath;
            }
            else
            {
                var filePath = Path.Combine(targetDirectory, $"Snippet_{stamp}_{unique}.txt");
                File.WriteAllText(filePath, text, System.Text.Encoding.UTF8);
                return filePath;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "保存文本片段为文件失败");
            return null;
        }
    }

    /// <summary>把 BitmapSource 存为 PNG 图片文件。</summary>
    public static string? SaveImageSnippet(BitmapSource image, string targetDirectory)
    {
        if (image is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(targetDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var unique = Guid.NewGuid().ToString("N")[..4];
            var filePath = Path.Combine(targetDirectory, $"Image_{stamp}_{unique}.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(filePath);
            encoder.Save(stream);
            return filePath;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "保存图片为 PNG 失败");
            return null;
        }
    }
}
