using FileStash.Core.Models;
using FileStash.Core.Resources;
using FileStash.Core.Utils;

namespace FileStash.Core.Services;

public sealed partial class ApplyExecutor
{
    public async Task<IReadOnlyList<RetryEntry>> PrepareRetryAsync(string operationId, CancellationToken ct, bool inspectTargets = true)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            await using var lease = await StorageWriteLease.AcquireAsync(_blobStore, ct);
            if (inspectTargets) await RecoverOperationCoreAsync(operationId, ct);
            var operation = await _operations.GetAsync(operationId, ct);
            if (operation is null || operation.Status == OperationStatus.Undone) return [];
            var selection = new List<RetryEntry>();
            foreach (var entry in await _operationEntries.GetByOperationAsync(operationId, ct))
            {
                if (entry.Action != EntryResult.Failed || entry.Pending || entry.UndoPending || entry.Undone
                    || entry.IntendedAction is not (PlannedAction.Create or PlannedAction.Overwrite or PlannedAction.Rename)
                    || !HashUtil.IsValidSha256(entry.NewSha256)) continue;
                if (entry.TargetPath.Any(char.IsControl) || !Path.IsPathFullyQualified(entry.TargetPath)
                    || Path.GetFullPath(entry.TargetPath) != entry.TargetPath)
                    throw new InvalidOperationException(Strings.RetryAuthorizationChanged);
                var hash = inspectTargets && File.Exists(entry.TargetPath)
                    ? await HashUtil.ComputeFileSha256Async(entry.TargetPath, ct) : null;
                selection.Add(new RetryEntry(entry.Id, entry.TargetPath, entry.IntendedAction.Value,
                    entry.NewSha256!, hash, inspectTargets ? _writer.GetIdentity(entry.TargetPath) : null, inspectTargets,
                    entry.NewZoneIdentifier?.ToArray(), inspectTargets && File.Exists(entry.TargetPath)
                        ? await ZoneIdentifier.ReadAsync(entry.TargetPath, ct) : null));
            }
            return selection.AsReadOnly();
        }
        finally { WriteLock.Release(); }
    }

    public async Task<int> RetryApprovedAsync(string operationId, IReadOnlyList<RetryEntry> approved, CancellationToken ct)
    {
        var selection = approved.Select(entry => entry with
        {
            ZoneIdentifier = entry.ZoneIdentifier?.ToArray(),
            CurrentZoneIdentifier = entry.CurrentZoneIdentifier?.ToArray()
        }).ToArray();
        if (selection.Length == 0) return 0;
        if (selection.Select(entry => entry.Id).Distinct().Count() != selection.Length)
            throw new InvalidOperationException(Strings.RetryAuthorizationChanged);
        await WriteLock.WaitAsync(ct);
        try
        {
            await using var lease = await StorageWriteLease.AcquireAsync(_blobStore, ct);
            var operation = await _operations.GetAsync(operationId, ct);
            if (operation is null || operation.Status == OperationStatus.Undone)
                throw new InvalidOperationException(Strings.RetryAuthorizationChanged);
            var entries = await _operationEntries.GetByOperationAsync(operationId, ct);
            for (var index = 0; index < selection.Length; index++)
            {
                var item = selection[index];
                var row = entries.SingleOrDefault(entry => entry.Id == item.Id);
                if (row is null || row.Action != EntryResult.Failed || row.Pending || row.UndoPending || row.Undone
                    || row.TargetPath != item.TargetPath || row.IntendedAction != item.Action || row.NewSha256 != item.Sha256
                    || !ZoneIdentifier.Matches(row.NewZoneIdentifier, item.ZoneIdentifier))
                    throw new InvalidOperationException(Strings.RetryAuthorizationChanged);
                var hash = File.Exists(item.TargetPath)
                    ? await HashUtil.ComputeFileSha256Async(item.TargetPath, ct) : null;
                var identity = _writer.GetIdentity(item.TargetPath);
                var zone = File.Exists(item.TargetPath) ? await ZoneIdentifier.ReadAsync(item.TargetPath, ct) : null;
                if (item.TargetInspected && (hash != item.CurrentSha256 || identity != item.CurrentIdentity
                    || !ZoneIdentifier.Matches(zone, item.CurrentZoneIdentifier)))
                    throw new InvalidOperationException(Strings.RetryAuthorizationChanged);
                selection[index] = item with { CurrentSha256 = hash, CurrentIdentity = identity, CurrentZoneIdentifier = zone, TargetInspected = true };
            }
            var succeeded = 0;
            foreach (var item in selection)
            {
                ct.ThrowIfCancellationRequested();
                var entry = entries.Single(entry => entry.Id == item.Id);
                var planned = new PlannedEntry
                {
                    Source = new StashEntry { Sha256 = item.Sha256, ZoneIdentifier = item.ZoneIdentifier },
                    Action = item.Action,
                    TargetPath = item.TargetPath,
                    State = item.Action == PlannedAction.Overwrite ? PlannedState.Overwrite : PlannedState.New,
                    ExpectedTargetSha256 = item.CurrentSha256,
                    ExpectedTargetZoneIdentifier = item.CurrentZoneIdentifier,
                    RequireBackup = true,
                };
                var created = new List<OperationEntry>();
                entry.Action = item.Action == PlannedAction.Overwrite ? EntryResult.Overwritten
                    : item.Action == PlannedAction.Rename ? EntryResult.Renamed : EntryResult.Created;
                entry.Error = null;
                await ExecuteEntryAsync(operationId, ExecutionEntry.Capture(planned), created, ct, entry);
                await _operationEntries.UpdateAsync(entry, CancellationToken.None);
                await _operationEntries.AddManyAsync(created.Where(row => row.Id == 0).ToList(), CancellationToken.None);
                if (entry.Action is EntryResult.Created or EntryResult.Overwritten or EntryResult.Renamed) succeeded++;
            }
            var updated = await _operationEntries.GetByOperationAsync(operationId, ct);
            operation.Status = updated.Any(entry => entry.Action == EntryResult.Failed || entry.Pending || entry.UndoPending)
                ? OperationStatus.PartiallyFailed : OperationStatus.Succeeded;
            operation.FinishedAt = DateTimeOffset.UtcNow;
            operation.Summary = BuildSummary(
                updated.Count(entry => entry.Action == EntryResult.Created),
                updated.Count(entry => entry.Action is EntryResult.Overwritten or EntryResult.Renamed),
                updated.Count(entry => entry.Action == EntryResult.Skipped),
                updated.Count(entry => entry.Action == EntryResult.Failed || entry.Pending || entry.UndoPending));
            await _operations.UpdateAsync(operation, CancellationToken.None);
            return succeeded;
        }
        finally { WriteLock.Release(); }
    }
}
