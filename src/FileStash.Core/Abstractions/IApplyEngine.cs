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

    /// <summary>准备待重试的条目清单，可预先检测目标当前状态与文件标识。</summary>
    Task<IReadOnlyList<RetryEntry>> PrepareRetryAsync(string operationId, CancellationToken ct, bool inspectTargets = true);

    /// <summary>执行经授权/确认的重试条目。</summary>
    Task<int> RetryApprovedAsync(string operationId, IReadOnlyList<RetryEntry> approved, CancellationToken ct);

    /// <summary>恢复未决的写入或撤销事务日志，使数据库状态与实际文件系统一致。</summary>
    Task<int> RecoverAsync(CancellationToken ct);

    /// <summary>保留目标文件当前内容，将该条目从重试候选标记为已解决。</summary>
    Task KeepCurrentAsync(string operationId, long entryId, CancellationToken ct);
}

/// <summary>
/// 原子替换文件。同卷的临时文件 → 目标路径，目标已存在时保留目标原有的属性 / ACL / 创建时间。
/// </summary>
public interface IAtomicFileWriter
{
    Task<bool> ReplaceAsync(string temporaryPath, string targetPath, CancellationToken ct);

    /// <summary>仅在目标文件不存在时安全创建。</summary>
    Task<bool> CreateAsync(string temporaryPath, string targetPath, CancellationToken ct);

    /// <summary>获取文件的唯一系统标识（如卷序列号与文件索引），用于崩溃恢复见证与一致性比对。</summary>
    string? GetIdentity(string path) => null;

    /// <summary>锁定并保护目标祖先目录与文件链，防止并发路径切换或符号链接竞态攻击。</summary>
    IDisposable? GuardTarget(string path) => null;
}

/// <summary>查是谁占着这个文件。</summary>
public interface IFileLockInspector
{
    /// <summary>返回占用该文件的进程名；没人占用则返回空列表。</summary>
    Task<IReadOnlyList<string>> GetLockingProcessesAsync(string path, CancellationToken ct);
}
