using Dapper;
using FileStash.Core.Utils;
using Microsoft.Data.Sqlite;

namespace FileStash.Infrastructure.Data;

/// <summary>
/// SQLite 连接工厂。每次打开都带上 foreign_keys / busy_timeout 两个 PRAGMA。
/// </summary>
public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    static SqliteConnectionFactory()
    {
        // Dapper 的类型处理器是全局状态，注册一次即可
        SqlMapper.AddTypeHandler(new Iso8601DateTimeOffsetTypeHandler());
    }

    private readonly string _databasePath;

    public SqliteConnectionFactory(AppPaths paths)
    {
        _databasePath = paths.DatabasePath;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
        }.ToString());

        await connection.OpenAsync(ct);

        // foreign_keys：SQLite 默认关着，不开的话外键约束形同虚设
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys=ON;", ct);

        // busy_timeout：主进程与提权重试进程会同时访问同一个库，
        // 没有它就是直接 SQLITE_BUSY 抛出来
        await ExecutePragmaAsync(connection, "PRAGMA busy_timeout=5000;", ct);

        return connection;
    }

    private static async Task ExecutePragmaAsync(SqliteConnection connection, string pragma, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = pragma;
        await command.ExecuteNonQueryAsync(ct);
    }
}
