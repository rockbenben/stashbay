using FileStash.Core.Models;

namespace FileStash.Core.Abstractions;

public interface IApplyPlanner
{
    /// <summary>生成写入计划（不落盘）。</summary>
    /// <param name="skipIdentical">false 时，内容已经一模一样的文件也按覆盖重写一遍。</param>
    Task<ApplyPlan> BuildPlanAsync(
        IReadOnlyList<StashItem> items,
        TargetSpec target,
        ConflictPolicy policy,
        CancellationToken ct,
        bool skipIdentical = true);
}

public interface IApplyExecutor
{
    /// <summary>执行计划，返回记录下来的操作（可用于撤销）。</summary>
    Task<Operation> ExecuteAsync(ApplyPlan plan, IProgress<ProgressInfo>? progress, CancellationToken ct);

    /// <summary>撤销一次操作。</summary>
    Task<UndoResult> UndoAsync(string operationId, CancellationToken ct);

    /// <summary>重试一次操作里失败的条目（"以管理员身份重试失败项"走这里）。</summary>
    Task<int> RetryFailedAsync(string operationId, CancellationToken ct);
    Task<IReadOnlyList<RetryEntry>> PrepareRetryAsync(string operationId, CancellationToken ct, bool inspectTargets = true);
    Task<int> RetryApprovedAsync(string operationId, IReadOnlyList<RetryEntry> approved, CancellationToken ct);
    Task<int> RecoverAsync(CancellationToken ct);
    Task KeepCurrentAsync(string operationId, long entryId, CancellationToken ct);
}

/// <summary>
/// 原子替换文件。同卷的临时文件 → 目标路径，目标已存在时保留目标原有的属性 / ACL / 创建时间。
/// </summary>
public interface IAtomicFileWriter
{
    Task<bool> ReplaceAsync(string temporaryPath, string targetPath, CancellationToken ct);

    /// <summary>Only commit when the destination is still absent.</summary>
    Task<bool> CreateAsync(string temporaryPath, string targetPath, CancellationToken ct);
    string? GetIdentity(string path) => null;
    IDisposable? GuardTarget(string path) => null;

}

/// <summary>查是谁占着这个文件。</summary>
public interface IFileLockInspector
{
    /// <summary>返回占用该文件的进程名；没人占用则返回空列表。</summary>
    Task<IReadOnlyList<string>> GetLockingProcessesAsync(string path, CancellationToken ct);
}
