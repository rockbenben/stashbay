using Microsoft.Data.Sqlite;

namespace FileStash.Infrastructure.Data;

/// <summary>打开 SQLite 连接。</summary>
public interface ISqliteConnectionFactory
{
    Task<SqliteConnection> OpenAsync(CancellationToken ct);
}
