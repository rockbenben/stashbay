using FileStash.Core.Abstractions;
using FileStash.Core.Models;
using FileStash.Core.Resources;
using FileStash.Core.Utils;

namespace FileStash.Core.Services;

/// <summary>
/// 执行写入计划。备份 → 原子写入 → 记录历史；可撤销。
/// </summary>
public sealed partial class ApplyExecutor : IApplyExecutor
{
    /// <summary>
    /// 全局写锁：所有 Apply / Undo 串行化，避免两个操作同时写同一目标路径。
    /// </summary>
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    private readonly IBlobStore _blobStore;
    private readonly IOperationRepository _operations;
    private readonly IOperationEntryRepository _operationEntries;
    private readonly IAtomicFileWriter _writer;
    private readonly IFileLockInspector _lockInspector;
    private readonly ISettingsService _settings;
    private readonly IGitService? _git;

    public ApplyExecutor(
        IBlobStore blobStore,
        IOperationRepository operations,
        IOperationEntryRepository operationEntries,
        IAtomicFileWriter writer,
        IFileLockInspector lockInspector,
        ISettingsService settings,
        IGitService? git = null)
    {
        _blobStore = blobStore;
        _operations = operations;
        _operationEntries = operationEntries;
        _writer = writer;
        _lockInspector = lockInspector;
        _settings = settings;
        _git = git;
    }

    public async Task<Operation> ExecuteAsync(
        ApplyPlan plan,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct)
    {
        await WriteLock.WaitAsync(ct);

        try
        {
            await using var processLock = await StorageWriteLease.AcquireAsync(_blobStore, ct);
            return await ExecuteCoreAsync(plan, progress, ct);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private async Task<Operation> ExecuteCoreAsync(
        ApplyPlan plan,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct)
    {
        var operation = new Operation
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedAt = DateTimeOffset.UtcNow,
            TargetMode = plan.Target.Mode,
            TargetDescription = DescribeTarget(plan.Target),
            Status = OperationStatus.Running,
        };

        await _operations.AddAsync(operation, ct);

        var entries = new List<OperationEntry>();
        var created = 0;
        var overwritten = 0;
        var skipped = 0;
        var failed = 0;
        var cancelled = false;

        var total = plan.Entries.Count;
        var done = 0;

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var planned in plan.Entries)
        {
            if (planned.Action != PlannedAction.Skip && !destinations.Add(Path.GetFullPath(planned.TargetPath)))
                planned.State = PlannedState.Invalid;
        }
        var journal = plan.Entries.Select(planned => new OperationEntry
        {
            OperationId = operation.Id,
            TargetPath = planned.TargetPath,
            IntendedAction = planned.State is PlannedState.Invalid or PlannedState.TypeConflict
                ? PlannedAction.Skip : planned.Action,
            Action = planned.Action == PlannedAction.Skip || planned.State is PlannedState.Invalid or PlannedState.TypeConflict
                ? EntryResult.Skipped : EntryResult.Failed,
            NewSha256 = planned.Source.Sha256,
            PreviousSha256 = planned.ExpectedTargetSha256,
            Error = planned.Action == PlannedAction.Skip ? null : Strings.RecoveryNotCommitted,
        }).ToList();
        await _operationEntries.AddManyAsync(journal, ct);

        for (var index = 0; index < plan.Entries.Count; index++)
        {
            var planned = plan.Entries[index];
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            (OperationEntry Entry, bool Failed) result;
            try
            {
                result = await ExecuteEntryAsync(operation.Id, planned, entries, ct, journal[index]);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            entries.Add(result.Entry);
            if (result.Entry.Id == 0)
                await _operationEntries.AddManyAsync([result.Entry], CancellationToken.None);
            else
                await _operationEntries.UpdateAsync(result.Entry, CancellationToken.None);

            switch (result.Entry.Action)
            {
                case EntryResult.Created:
                    created++;
                    break;
                case EntryResult.Overwritten:
                case EntryResult.Renamed:
                    overwritten++;
                    break;
                case EntryResult.Skipped:
                    skipped++;
                    break;
                case EntryResult.Failed:
                    failed++;
                    break;
            }

            done++;
            progress?.Report(new ProgressInfo(done, total, planned.TargetPath));
        }

        // 记账必须完成：即使被取消，已经写下去的东西也要留下记录（否则撤不回来）。
        // 所以这里用 CancellationToken.None，不跟着取消走。
        await _operationEntries.AddManyAsync(entries.Where(entry => entry.Id == 0).ToList(), CancellationToken.None);

        operation.FinishedAt = DateTimeOffset.UtcNow;
        operation.Status = cancelled
            ? OperationStatus.Cancelled
            : failed > 0
                ? OperationStatus.PartiallyFailed
                : OperationStatus.Succeeded;
        operation.Summary = BuildSummary(created, overwritten, skipped, failed);

        await _operations.UpdateAsync(operation, CancellationToken.None);

        // 写入后 git add：只处理仓库内的文件
        await TryGitAddAsync(plan, entries);

        return operation;
    }

    /// <summary>写入后把成功写入的文件加入 git 暂存区（只限仓库内）。</summary>
    private async Task TryGitAddAsync(ApplyPlan plan, IReadOnlyList<OperationEntry> entries)
    {
        if (!_settings.Current.AutoGitAddAfterApply || _git is null || !_git.IsAvailable)
        {
            return;
        }

        if (plan.GitContext is not { } context || string.IsNullOrEmpty(context.RepoRoot))
        {
            return;
        }

        var relativePaths = entries
            .Where(entry => entry.Action is EntryResult.Created or EntryResult.Overwritten or EntryResult.Renamed)
            .Select(entry => entry.TargetPath)
            .Where(path => PathUtil.IsUnder(context.RepoRoot, path))
            .Select(path => PathUtil.GetRelative(context.RepoRoot, path))
            .ToList();

        if (relativePaths.Count == 0)
        {
            return;
        }

        try
        {
            await _git.AddAsync(context.RepoRoot, relativePaths, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // git add 失败不该让整次写入失败
        }
    }

    private async Task<(OperationEntry Entry, bool Failed)> ExecuteEntryAsync(
        string operationId,
        PlannedEntry planned,
        List<OperationEntry> entries,
        CancellationToken ct, OperationEntry? existing = null)
    {
        var entry = existing ?? new OperationEntry
        {
            OperationId = operationId,
            TargetPath = planned.TargetPath,
            Action = planned.Action switch
            {
                PlannedAction.Create => EntryResult.Created,
                PlannedAction.Overwrite => EntryResult.Overwritten,
                PlannedAction.Rename => EntryResult.Renamed,
                _ => EntryResult.Skipped,
            },
            NewSha256 = planned.Source.Sha256,
            IntendedAction = planned.Action,
        };

        entry.Action = planned.Action switch
        {
            PlannedAction.Create => EntryResult.Created,
            PlannedAction.Overwrite => EntryResult.Overwritten,
            PlannedAction.Rename => EntryResult.Renamed,
            _ => EntryResult.Skipped,
        };
        entry.Error = null;
        var committedAction = entry.Action;
        if (planned.Action == PlannedAction.Skip
            || planned.State is PlannedState.Invalid or PlannedState.TypeConflict
            || (planned.Action == PlannedAction.Overwrite && planned.State == PlannedState.New))
        {
            entry.Action = EntryResult.Skipped;
            return (entry, false);
        }

        if (planned.Source.IsDirectory || planned.Source.Sha256 is null)
        {
            // 空目录：只要把目录建出来
            try
            {
                EnsureDirectory(planned.TargetPath, entries, operationId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                entry.Action = EntryResult.Failed;
                entry.Error = await DescribeFailureAsync(planned.TargetPath, ex);
                return (entry, true);
            }

            return (entry, false);
        }

        var directory = Path.GetDirectoryName(planned.TargetPath);

        if (string.IsNullOrEmpty(directory))
        {
            entry.Action = EntryResult.Failed;
            entry.Error = Strings.NoParentDirectory;
            return (entry, true);
        }

        try
        {
            using var targetGuard = _writer.GuardTarget(planned.TargetPath);
            if (planned.Action == PlannedAction.Overwrite && !File.Exists(planned.TargetPath))
                throw new FileNotFoundException(Strings.Format(Strings.TargetMissing, planned.TargetPath));
            if (planned.Action == PlannedAction.Overwrite && planned.ExpectedTargetSha256 is not null
                && !string.Equals(await HashUtil.ComputeFileSha256Async(planned.TargetPath, ct),
                    planned.ExpectedTargetSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException(Strings.UncommittedChangesWarning);

            // 父目录（新建的要记进历史，撤销时删掉）
            EnsureDirectory(directory, entries, operationId);
            using var completedTargetGuard = _writer.GuardTarget(planned.TargetPath);

            await _operationEntries.AddManyAsync(entries.Where(created => created.Id == 0).ToList(), CancellationToken.None);

            // 覆盖前先备份
            if (planned.Action == PlannedAction.Overwrite
                && (_settings.Current.BackupBeforeOverwrite || planned.RequireBackup)
                && File.Exists(planned.TargetPath))
            {
                var (backupSha, _) = await _blobStore.PutFileAsync(planned.TargetPath, ct);
                entry.BackupSha256 = backupSha;
            }

            var temporaryPath = CreateTemporaryPath(directory);
            try
            {
                await _blobStore.CopyBlobToAsync(planned.Source.Sha256, temporaryPath, ct);
                if (!string.Equals(await HashUtil.ComputeFileSha256Async(temporaryPath, ct),
                        planned.Source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(Strings.BlobIntegrityFailed);

                entry.WitnessPath = temporaryPath;
                entry.WitnessIdentity = _writer.GetIdentity(temporaryPath);
                entry.PreviousSha256 = File.Exists(planned.TargetPath)
                    ? await HashUtil.ComputeFileSha256Async(planned.TargetPath, ct) : null;
                entry.Pending = true;
                entry.Action = EntryResult.Skipped;
                if (entry.Id == 0)
                    await _operationEntries.AddManyAsync([entry], CancellationToken.None);
                else
                    await _operationEntries.UpdateAsync(entry, CancellationToken.None);

                if (planned.Action == PlannedAction.Overwrite)
                    await WriteWithReadOnlyHandlingAsync(temporaryPath, planned.TargetPath, ct);
                else if (!await _writer.CreateAsync(temporaryPath, planned.TargetPath, ct))
                    throw new IOException(Strings.WriteFailedDetail);
            }
            finally
            {
                if (!entry.Pending) TryDelete(temporaryPath);
            }
        }
        catch (OperationCanceledException)
        {
            entry.Action = EntryResult.Failed;
            entry.Error = Strings.Cancelled;
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entry.Action = EntryResult.Failed;
            entry.Error = await DescribeFailureAsync(planned.TargetPath, ex);
            return (entry, true);
        }

        entry.Pending = false;
        entry.Action = committedAction;
        return (entry, false);
    }

    /// <summary>
    /// 只读目标：先清掉只读属性，写完再恢复。
    /// </summary>
    /// <remarks>
    /// 顺序不能反：覆盖只读文件会返回 ERROR_ACCESS_DENIED，必须先清除。
    /// </remarks>
    private async Task WriteWithReadOnlyHandlingAsync(string temporaryPath, string targetPath, CancellationToken ct)
    {
        var wasReadOnly = false;

        if (File.Exists(targetPath))
        {
            var attributes = File.GetAttributes(targetPath);
            wasReadOnly = attributes.HasFlag(FileAttributes.ReadOnly);

            if (wasReadOnly)
            {
                File.SetAttributes(targetPath, attributes & ~FileAttributes.ReadOnly);
            }
        }

        try
        {
            var replaced = await _writer.ReplaceAsync(temporaryPath, targetPath, ct);

            if (!replaced)
            {
                throw new IOException($"Failed to atomically replace '{targetPath}'.");
            }
        }
        finally
        {
            if (wasReadOnly && File.Exists(targetPath))
            {
                try
                {
                    File.SetAttributes(targetPath, File.GetAttributes(targetPath) | FileAttributes.ReadOnly);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>确保目录存在，把新建的目录记进历史（撤销时按逆序删掉）。</summary>
    private static void EnsureDirectory(string directory, List<OperationEntry> entries, string operationId)
    {
        if (Directory.Exists(directory))
        {
            return;
        }

        // 找出第一个已经存在的祖先，把中间每一层都记下来
        var missing = new List<string>();
        var current = directory;

        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            missing.Add(current);

            var parent = Path.GetDirectoryName(current);

            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        Directory.CreateDirectory(directory);

        foreach (var created in missing.OrderBy(path => path.Length))
        {
            entries.Add(new OperationEntry
            {
                OperationId = operationId,
                TargetPath = created,
                Action = EntryResult.DirectoryCreated,
                CreatedDirectory = true,
            });
        }
    }

    private async Task<string> DescribeFailureAsync(string path, Exception exception)
    {
        if (exception is UnauthorizedAccessException)
        {
            return Strings.NoPermission;
        }

        var locking = await _lockInspector.GetLockingProcessesAsync(path, CancellationToken.None);

        if (locking.Count > 0)
        {
            return Strings.Format(Strings.LockedByProcess, string.Join(Strings.ListSeparator, locking));
        }

        // 查不到占用者不等于没被占用：32/33 就是共享冲突，只是拿不到进程名。
        // 这时候把 .NET 的整句英文抛到中文界面上，用户读不到重点。
        if (IsSharingViolation(exception))
        {
            return Strings.Format(Strings.LockedByProcess, Strings.UnknownProcess);
        }

        return Strings.Format(Strings.WriteFailedDetail, exception.Message);
    }

    /// <summary>ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION。</summary>
    private static bool IsSharingViolation(Exception exception) =>
        exception is IOException
        && exception.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021);

    /// <summary>
    /// 重试一次操作里失败的条目（"以管理员身份重试失败项"走这里）。
    /// </summary>
    /// <returns>成功重写的条目数。</returns>
    public async Task<int> RetryFailedAsync(string operationId, CancellationToken ct) =>
        await RetryApprovedAsync(operationId, await PrepareRetryAsync(operationId, ct), ct);

    public async Task<UndoResult> UndoAsync(string operationId, CancellationToken ct)
    {
        await WriteLock.WaitAsync(ct);

        try
        {
            await using var processLock = await StorageWriteLease.AcquireAsync(_blobStore, ct);
            return await UndoCoreAsync(operationId, ct);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private async Task<UndoResult> UndoCoreAsync(string operationId, CancellationToken ct)
    {
        await RecoverOperationCoreAsync(operationId, ct);
        var operation = await _operations.GetAsync(operationId, ct);

        if (operation is null)
        {
            return new UndoResult { Failed = 1, Messages = [Strings.OperationNotFound] };
        }

        var entries = await _operationEntries.GetByOperationAsync(operationId, ct);

        var counters = new UndoCounters();

        // 逆序处理：先删文件，再删目录
        var undoOrder = entries.Where(entry => entry.Action != EntryResult.DirectoryCreated).Reverse()
            .Concat(entries.Where(entry => entry.Action == EntryResult.DirectoryCreated)
                .OrderByDescending(entry => entry.TargetPath.Length));
        foreach (var entry in undoOrder)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Undone) continue;
            if (entry.Pending || entry.UndoPending)
            {
                counters.Failed++;
                counters.Messages.Add(Strings.RecoveryNeedsReview);
                continue;
            }
            entry.UndoPending = true;
            await _operationEntries.UpdateAsync(entry, CancellationToken.None);
            var issuesBefore = counters.Failed + counters.Skipped;
            switch (entry.Action)
            {
                case EntryResult.DirectoryCreated:
                    UndoDirectory(entry, counters);
                    break;

                case EntryResult.Created:
                case EntryResult.Renamed:
                    await UndoCreatedAsync(entry, counters, ct);
                    break;

                case EntryResult.Overwritten:
                    await UndoOverwrittenAsync(entry, counters, ct);
                    break;
            }
            entry.Undone = counters.Failed + counters.Skipped == issuesBefore;
            entry.UndoPending = !entry.Undone;
            entry.Error = entry.Undone ? null : Strings.RecoveryNeedsReview;
            await _operationEntries.UpdateAsync(entry, CancellationToken.None);
        }

        operation.Status = counters.Failed + counters.Skipped > 0 ? OperationStatus.PartiallyFailed : OperationStatus.Undone;
        operation.FinishedAt = DateTimeOffset.UtcNow;

        await _operations.UpdateAsync(operation, ct);

        return new UndoResult
        {
            Restored = counters.Restored,
            Deleted = counters.Deleted,
            Skipped = counters.Skipped,
            Failed = counters.Failed,
            Messages = counters.Messages,
        };
    }

    /// <summary>撤销过程中的计数（异步方法不能用 ref/out 参数）。</summary>
    private sealed class UndoCounters
    {
        public int Restored { get; set; }

        public int Deleted { get; set; }

        public int Skipped { get; set; }

        public int Failed { get; set; }

        public List<string> Messages { get; } = [];
    }

    private static void UndoDirectory(OperationEntry entry, UndoCounters counters)
    {
        if (!Directory.Exists(entry.TargetPath))
        {
            return;
        }

        try
        {
            if (Directory.EnumerateFileSystemEntries(entry.TargetPath).Any())
            {
                counters.Skipped++;
                counters.Messages.Add(Strings.Format(Strings.DirNotEmpty, entry.TargetPath));
                return;
            }

            Directory.Delete(entry.TargetPath);
            counters.Deleted++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            counters.Failed++;
            counters.Messages.Add(Strings.Format(Strings.DeleteDirFailed, entry.TargetPath, ex.Message));
        }
    }

    private async Task UndoCreatedAsync(OperationEntry entry, UndoCounters counters, CancellationToken ct)
    {
        using var targetGuard = _writer.GuardTarget(entry.TargetPath);
        if (!File.Exists(entry.TargetPath))
        {
            return;
        }

        if (!await IsUnchangedAsync(entry, ct))
        {
            counters.Skipped++;
            counters.Messages.Add(Strings.Format(Strings.FileModifiedNotDeleted, entry.TargetPath));
            return;
        }

        try
        {
            File.Delete(entry.TargetPath);
            counters.Deleted++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            counters.Failed++;
            counters.Messages.Add(Strings.Format(Strings.DeleteFileFailed, entry.TargetPath, ex.Message));
        }
    }

    private async Task UndoOverwrittenAsync(OperationEntry entry, UndoCounters counters, CancellationToken ct)
    {
        using var targetGuard = _writer.GuardTarget(entry.TargetPath);
        if (entry.BackupSha256 is null)
        {
            counters.Skipped++;
            counters.Messages.Add(Strings.NoBackup);
            return;
        }

        if (!File.Exists(entry.TargetPath))
        {
            counters.Skipped++;
            counters.Messages.Add(Strings.Format(Strings.TargetMissing, entry.TargetPath));
            return;
        }

        if (!await IsUnchangedAsync(entry, ct))
        {
            counters.Skipped++;
            counters.Messages.Add(Strings.Format(Strings.FileModifiedNotRestored, entry.TargetPath));
            return;
        }

        if (!_blobStore.Exists(entry.BackupSha256))
        {
            counters.Skipped++;
            counters.Messages.Add(Strings.Format(Strings.BackupExpired, entry.TargetPath));
            return;
        }

        var directory = Path.GetDirectoryName(entry.TargetPath);

        if (string.IsNullOrEmpty(directory))
        {
            counters.Failed++;
            return;
        }

        var temporaryPath = CreateTemporaryPath(directory);

        try
        {
            try
            {
                await _blobStore.CopyBlobToAsync(entry.BackupSha256, temporaryPath, ct);
                if (await HashUtil.ComputeFileSha256Async(temporaryPath, ct) != entry.BackupSha256)
                    throw new IOException(Strings.BlobIntegrityFailed);
                await WriteWithReadOnlyHandlingAsync(temporaryPath, entry.TargetPath, ct);
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            counters.Restored++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            counters.Failed++;
            counters.Messages.Add(Strings.Format(Strings.RestoreFailed, entry.TargetPath, ex.Message));
        }
    }

    /// <summary>目标文件当前内容是否仍然是本次写入的内容。</summary>
    private async Task<bool> IsUnchangedAsync(OperationEntry entry, CancellationToken ct)
    {
        if (entry.NewSha256 is null
            || (entry.WitnessIdentity is not null && _writer.GetIdentity(entry.TargetPath) != entry.WitnessIdentity))
        {
            return false;
        }

        var sha256 = await HashUtil.ComputeFileSha256Async(entry.TargetPath, ct);

        return string.Equals(sha256, entry.NewSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSummary(int created, int overwritten, int skipped, int failed) =>
        $"{{\"created\":{created},\"overwritten\":{overwritten},\"skipped\":{skipped},\"failed\":{failed}}}";

    private static string DescribeTarget(TargetSpec target) => target.Mode switch
    {
        TargetMode.OriginalLocation => Strings.TargetOriginalLocation,
        TargetMode.AnotherRoot => PathMapper.ExpandTargetPath(target.TargetPath),
        TargetMode.SpecifiedPath => PathMapper.ExpandTargetPath(target.TargetPath),
        TargetMode.DragExport => Strings.TargetDragExport,
        _ => target.Mode.ToString(),
    };

    private static string CreateTemporaryPath(string directory)
    {
        var path = Path.Combine(directory, $".{Guid.NewGuid():N}.fstmp~");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
