using FileStash.Core.Models;
using FileStash.Core.Resources;
using FileStash.Core.Utils;

namespace FileStash.Core.Services;

public sealed partial class ApplyExecutor
{
    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            await using var lease = await StorageWriteLease.AcquireAsync(_blobStore, ct);
            var recovered = 0;
            foreach (var operation in await _operations.GetAllAsync(ct))
                recovered += await RecoverOperationCoreAsync(operation.Id, ct);
            return recovered;
        }
        finally { WriteLock.Release(); }
    }

    private async Task<int> RecoverOperationCoreAsync(string operationId, CancellationToken ct)
    {
        var operation = await _operations.GetAsync(operationId, ct);
        if (operation is null || operation.Status == OperationStatus.Undone) return 0;
        var entries = await _operationEntries.GetByOperationAsync(operationId, ct);
        var changed = 0;
        foreach (var entry in entries.Where(entry => entry.Pending || entry.UndoPending))
        {
            try
            {
                var currentHash = File.Exists(entry.TargetPath)
                    ? await HashUtil.ComputeFileSha256Async(entry.TargetPath, ct) : null;
                if (entry.UndoPending)
                {
                    var deletionCompleted = entry.Action is EntryResult.Created or EntryResult.Renamed
                        && !File.Exists(entry.TargetPath) && !Directory.Exists(entry.TargetPath);
                    var restorationCompleted = entry.Action == EntryResult.Overwritten
                        && entry.BackupSha256 is not null && currentHash == entry.BackupSha256;
                    var directoryRemoved = entry.Action == EntryResult.DirectoryCreated
                        && !Directory.Exists(entry.TargetPath);
                    if (deletionCompleted || restorationCompleted || directoryRemoved)
                    {
                        entry.Undone = true;
                        entry.UndoPending = false;
                        entry.Error = null;
                    }
                    else if ((currentHash == entry.NewSha256 && currentHash is not null
                            && (entry.WitnessIdentity is null || _writer.GetIdentity(entry.TargetPath) == entry.WitnessIdentity))
                        || (entry.Action == EntryResult.DirectoryCreated && Directory.Exists(entry.TargetPath)
                            && !Directory.EnumerateFileSystemEntries(entry.TargetPath).Any()))
                    {
                        // The interrupted undo has not changed the file yet.
                        entry.UndoPending = false;
                        entry.Error = null;
                    }
                    else entry.Error = Strings.RecoveryNeedsReview;
                }
                else if (entry.WitnessIdentity is not null && currentHash == entry.NewSha256
                    && _writer.GetIdentity(entry.TargetPath) == entry.WitnessIdentity)
                {
                    entry.Action = entry.IntendedAction switch
                    {
                        PlannedAction.Create => EntryResult.Created,
                        PlannedAction.Rename => EntryResult.Renamed,
                        PlannedAction.Overwrite => EntryResult.Overwritten,
                        _ => EntryResult.Skipped,
                    };
                    entry.Pending = false;
                    entry.Error = null;
                }
                else if (entry.WitnessIdentity is not null && entry.WitnessPath is not null
                    && _writer.GetIdentity(entry.WitnessPath) == entry.WitnessIdentity
                    && currentHash == entry.PreviousSha256
                    && await HashUtil.ComputeFileSha256Async(entry.WitnessPath, ct) == entry.NewSha256)
                {
                    entry.Action = EntryResult.Failed;
                    entry.Pending = false;
                    entry.Error = Strings.RecoveryNotCommitted;
                }
                else entry.Error = Strings.RecoveryNeedsReview;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entry.Error = Strings.RecoveryNeedsReview;
            }
            await _operationEntries.UpdateAsync(entry, CancellationToken.None);
            changed++;
        }
        if (changed > 0 || operation.Status == OperationStatus.Running)
        {
            operation.FinishedAt = DateTimeOffset.UtcNow;
            operation.Status = entries.Count > 0 && entries.All(entry => entry.Undone)
                ? OperationStatus.Undone
                : entries.Any(entry => entry.Pending || entry.UndoPending || entry.Action == EntryResult.Failed)
                    ? OperationStatus.PartiallyFailed : OperationStatus.Cancelled;
            operation.Summary = BuildSummary(
                entries.Count(entry => !entry.Pending && entry.Action == EntryResult.Created),
                entries.Count(entry => !entry.Pending && entry.Action is EntryResult.Overwritten or EntryResult.Renamed),
                entries.Count(entry => !entry.Pending && entry.Action == EntryResult.Skipped),
                entries.Count(entry => entry.Pending || entry.UndoPending || entry.Action == EntryResult.Failed));
            await _operations.UpdateAsync(operation, CancellationToken.None);
        }
        return changed;
    }

    public async Task KeepCurrentAsync(string operationId, long entryId, CancellationToken ct)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            await using var lease = await StorageWriteLease.AcquireAsync(_blobStore, ct);
            var entry = (await _operationEntries.GetByOperationAsync(operationId, ct))
                .Single(entry => entry.Id == entryId);
            if (!entry.Pending && !entry.UndoPending) return;
            entry.Pending = false;
            entry.UndoPending = false;
            entry.Undone = true;
            entry.Action = EntryResult.Skipped;
            entry.Error = null;
            await _operationEntries.UpdateAsync(entry, CancellationToken.None);
            await RecoverOperationCoreAsync(operationId, ct);
            var rows = await _operationEntries.GetByOperationAsync(operationId, ct);
            var operation = await _operations.GetAsync(operationId, ct);
            if (operation is not null && rows.All(row => row.Undone))
            {
                operation.Status = OperationStatus.Undone;
                operation.FinishedAt = DateTimeOffset.UtcNow;
                await _operations.UpdateAsync(operation, CancellationToken.None);
            }
        }
        finally { WriteLock.Release(); }
    }
}
