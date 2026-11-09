using FileStash.Core.Abstractions;

namespace FileStash.Core.Services;

/// <summary>Coordinates blob publication, reference commits and collection across processes.</summary>
internal static class StorageWriteLease
{
    public static async Task<FileStream> AcquireAsync(IBlobStore blobs, CancellationToken ct)
    {
        var blobPath = blobs.GetBlobPath(new string('0', 64));
        var home = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(blobPath)))!;
        var lockPath = Path.Combine(home, "write.lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
            {
                await Task.Delay(50, ct);
            }
        }
    }
}
