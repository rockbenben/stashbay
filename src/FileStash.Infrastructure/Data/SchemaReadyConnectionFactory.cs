using Microsoft.Data.Sqlite;

namespace FileStash.Infrastructure.Data;

/// <summary>
/// 带"schema 就绪"门闩的连接工厂：先把迁移跑完，再把连接交出去。
/// </summary>
/// <remarks>
/// 为什么需要它：应用把数据库迁移放在启动后期做（不占启动路径），
/// 而主窗口一 Loaded 就会查表。查表早于迁移时，表还不存在，
/// 抛出来的是 "no such table: StashGroup" —— 用户看到的是一个模态错误框，
/// 而那个框会把本该跑起来的迁移一起卡住：模态框自己的消息循环不泵 WPF 的调度器，
/// 迁移排在 ApplicationIdle 上，永远轮不到执行。
///
/// 有了这道门闩，"查询早于迁移"就变成"查询等迁移"，顺序问题从根上消失，
/// 不再依赖"谁先跑"这种时序上的巧合。迁移本身只跑一次，之后的连接都是零开销。
/// </remarks>
public sealed class SchemaReadyConnectionFactory : ISqliteConnectionFactory
{
    // 注入具体的 SqliteConnectionFactory 而不是接口：
    // 接口在 DI 里指回本类，会绕成环。
    private readonly SqliteConnectionFactory _inner;
    private readonly DatabaseMigrator _migrator;

    public SchemaReadyConnectionFactory(SqliteConnectionFactory inner, DatabaseMigrator migrator)
    {
        _inner = inner;
        _migrator = migrator;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        await _migrator.EnsureReadyAsync(ct);
        return await _inner.OpenAsync(ct);
    }
}
