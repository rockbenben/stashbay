namespace FileStash.Core.Utils;

/// <summary>Preserves the raw Zone.Identifier stream independently of content hashes.</summary>
public static class ZoneIdentifier
{
    private const string StreamName = ":Zone.Identifier";

    public static bool Matches(byte[]? first, byte[]? second) =>
        first is null ? second is null : second is not null && first.AsSpan().SequenceEqual(second);

    public static async Task<byte[]?> ReadAsync(string path, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return await File.ReadAllBytesAsync(path + StreamName, ct); }
        catch (FileNotFoundException) when (File.Exists(path)) { return null; }
        catch (IOException ex) when (File.Exists(path)
            && (ex.HResult & 0xffff) is 1 or 50 or 123)
        {
            // A volume without named-stream support has no origin stream to read.
            return null;
        }
    }

    public static async Task WriteAsync(string path, byte[]? data, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (data is not null) throw new PlatformNotSupportedException();
            return;
        }
        if (Matches(await ReadAsync(path, ct), data)) return;
        var attributes = File.GetAttributes(path);
        var readOnly = attributes.HasFlag(FileAttributes.ReadOnly);
        if (readOnly) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        try
        {
            if (data is null) File.Delete(path + StreamName);
            else await File.WriteAllBytesAsync(path + StreamName, data, ct);
        }
        finally
        {
            if (readOnly) File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
    }
}
