using Dapper;
using Microsoft.Data.Sqlite;

namespace FileStash.Infrastructure.Data;

/// <summary>
/// 迁移器：按 SchemaVersion 顺序执行脚本，只跑没跑过的。
/// </summary>
public sealed class DatabaseMigrator
{
    // 用具体的连接工厂而不是接口：接口在 DI 里指向"带门闩"的那一层，
    // 迁移自己走那层会绕成环（迁移等门闩，门闩等迁移）。
    private readonly SqliteConnectionFactory _connectionFactory;

    private Task<int>? _migration;

    public DatabaseMigrator(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>
    /// 确保迁移已经跑完，返回当前 schema 版本。迁移至多执行一次，重复调用返回同一个任务。
    /// </summary>
    /// <remarks>
    /// 迁移是"进程级一次性动作"，不能被单个调用方的取消令牌打断 ——
    /// 迁一半的 schema 比慢一点更糟。所以真正的迁移用 <see cref="CancellationToken.None"/>，
    /// 调用方的令牌只用来放弃"等"。
    /// </remarks>
    public async Task<int> EnsureReadyAsync(CancellationToken ct)
    {
        var task = _migration ??= MigrateAsync(CancellationToken.None);
        return await task.WaitAsync(ct);
    }

    /// <summary>执行所有待应用的迁移，返回当前 schema 版本。</summary>
    public async Task<int> MigrateAsync(CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);

        // WAL 是持久属性，只在第一次建库时设置一次
        await using (var walCommand = connection.CreateCommand())
        {
            walCommand.CommandText = "PRAGMA journal_mode=WAL;";
            await walCommand.ExecuteNonQueryAsync(ct);
        }

        await EnsureVersionTableAsync(connection, ct);

        var applied = await GetAppliedVersionsAsync(connection, ct);
        var current = applied.Count == 0 ? 0 : applied.Max();

        if (current > 0 && Migrations.All.Any(migration => migration.Version > current))
        {
            await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = connection.DataSource + $".schema-v{current}-{Guid.NewGuid():N}.bak",
            }.ToString());
            await backup.OpenAsync(ct);
            connection.BackupDatabase(backup);
        }

        foreach (var migration in Migrations.All.OrderBy(m => m.Version))
        {
            if (applied.Contains(migration.Version))
            {
                continue;
            }

            await ApplyAsync(connection, migration, ct);
            current = migration.Version;
        }

        return current;
    }

    /// <summary>已应用的版本号。</summary>
    public async Task<IReadOnlyList<int>> GetAppliedVersionsAsync(CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);
        await EnsureVersionTableAsync(connection, ct);
        return await GetAppliedVersionsAsync(connection, ct);
    }

    private static async Task EnsureVersionTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER NOT NULL);";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<int>> GetAppliedVersionsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var versions = await connection.QueryAsync<int>(
            new CommandDefinition("SELECT Version FROM SchemaVersion ORDER BY Version;", cancellationToken: ct));

        return versions.AsList();
    }

    private static async Task ApplyAsync(SqliteConnection connection, Migration migration, CancellationToken ct)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = migration.Sql;
            await command.ExecuteNonQueryAsync(ct);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO SchemaVersion (Version) VALUES (@Version);";
            command.Parameters.AddWithValue("@Version", migration.Version);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}
