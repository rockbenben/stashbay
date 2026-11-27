namespace FileStash.Core.Utils;

public static class FileReplacement
{
    /// <summary>Atomically publishes a staged file from the same directory as the destination.</summary>
    public static void MoveOverwritingWithRetry(string stagedPath, string destination)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(stagedPath, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 4 &&
                ex.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021))
            {
                // Windows can briefly deny a rename while another handle has the file open.
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }
}
