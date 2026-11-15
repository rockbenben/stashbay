namespace FileStash.Infrastructure.Data;

/// <summary>一次迁移。</summary>
/// <param name="Version">版本号，从 1 开始。</param>
/// <param name="Sql">该版本的建表脚本，可以包含多条语句。</param>
public sealed record Migration(int Version, string Sql);

/// <summary>全部迁移脚本，按版本顺序执行。</summary>
public static class Migrations
{
    public static IReadOnlyList<Migration> All { get; } =
    [
        new Migration(1, SchemaV1),
        new Migration(2, """
            ALTER TABLE OperationEntry ADD COLUMN IntendedAction INTEGER;
            ALTER TABLE OperationEntry ADD COLUMN Pending INTEGER NOT NULL DEFAULT 0;
            """),
        new Migration(3, """
            ALTER TABLE OperationEntry ADD COLUMN WitnessPath TEXT;
            ALTER TABLE OperationEntry ADD COLUMN WitnessIdentity TEXT;
            ALTER TABLE OperationEntry ADD COLUMN PreviousSha256 TEXT;
            ALTER TABLE OperationEntry ADD COLUMN UndoPending INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE OperationEntry ADD COLUMN Undone INTEGER NOT NULL DEFAULT 0;
            """),
        new Migration(4, """
            ALTER TABLE StashEntry ADD COLUMN ZoneIdentifier BLOB;
            ALTER TABLE OperationEntry ADD COLUMN NewZoneIdentifier BLOB;
            ALTER TABLE OperationEntry ADD COLUMN BackupZoneIdentifier BLOB;
            ALTER TABLE OperationEntry ADD COLUMN PreviousZoneIdentifier BLOB;
            ALTER TABLE OperationEntry ADD COLUMN UndoWitnessIdentity TEXT;
            """),
        new Migration(5, "ALTER TABLE StashGroup ADD COLUMN ClosedAt TEXT;"),
        new Migration(6, """
            ALTER TABLE StashGroup ADD COLUMN Kind INTEGER NOT NULL DEFAULT 0;
            CREATE UNIQUE INDEX UX_StashGroup_Default ON StashGroup(Kind) WHERE Kind = 2;
            """),
    ];

    /// <summary>
    /// Migration v1：创建组、项、条目、预设与操作历史核心表架构。
    /// </summary>
    /// <remarks>
    /// StashItem 用 SourcePathKey（应用层按 InvariantCulture 大写归一化）做唯一性判断，
    /// 不用 SQLite 的 NOCASE —— NOCASE 只折叠 ASCII 字母，
    /// 非 ASCII 路径（Ä / ä、Σ / σ）在它眼里是不同的，重复项会漏判。
    /// </remarks>
    private const string SchemaV1 = """
        CREATE TABLE StashGroup (
          Id TEXT PRIMARY KEY,
          Name TEXT NOT NULL,
          Note TEXT,
          IsPinned INTEGER NOT NULL DEFAULT 0,
          SortOrder INTEGER NOT NULL DEFAULT 0,
          CreatedAt TEXT NOT NULL,
          UpdatedAt TEXT NOT NULL
        );

        CREATE TABLE StashItem (
          Id TEXT PRIMARY KEY,
          GroupId TEXT NOT NULL REFERENCES StashGroup(Id) ON DELETE CASCADE,
          Kind INTEGER NOT NULL,
          DisplayName TEXT NOT NULL,
          SourcePath TEXT NOT NULL,
          SourcePathKey TEXT NOT NULL,
          RootPath TEXT NOT NULL,
          RelativePath TEXT NOT NULL,
          GitRepoRoot TEXT,
          GitBranch TEXT,
          GitCommit TEXT,
          FileCount INTEGER NOT NULL,
          TotalSize INTEGER NOT NULL,
          AddedAt TEXT NOT NULL,
          SnapshotAt TEXT NOT NULL
        );
        CREATE UNIQUE INDEX UX_StashItem_Group_Source ON StashItem(GroupId, SourcePathKey);

        CREATE TABLE StashEntry (
          Id INTEGER PRIMARY KEY AUTOINCREMENT,
          ItemId TEXT NOT NULL REFERENCES StashItem(Id) ON DELETE CASCADE,
          RelativePath TEXT NOT NULL,
          IsDirectory INTEGER NOT NULL DEFAULT 0,
          Sha256 TEXT,
          Size INTEGER NOT NULL DEFAULT 0,
          SourceLastWriteUtc TEXT
        );
        CREATE INDEX IX_StashEntry_Item ON StashEntry(ItemId);
        CREATE INDEX IX_StashEntry_Sha ON StashEntry(Sha256);

        CREATE TABLE RootPreset (
          Id TEXT PRIMARY KEY,
          Name TEXT NOT NULL,
          Path TEXT NOT NULL,
          SortOrder INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE Operation (
          Id TEXT PRIMARY KEY,
          StartedAt TEXT NOT NULL,
          FinishedAt TEXT,
          GroupId TEXT,
          TargetMode INTEGER NOT NULL,
          TargetDescription TEXT NOT NULL,
          Status INTEGER NOT NULL,
          Summary TEXT
        );

        CREATE TABLE OperationEntry (
          Id INTEGER PRIMARY KEY AUTOINCREMENT,
          OperationId TEXT NOT NULL REFERENCES Operation(Id) ON DELETE CASCADE,
          TargetPath TEXT NOT NULL,
          Action INTEGER NOT NULL,
          NewSha256 TEXT,
          BackupSha256 TEXT,
          CreatedDirectory INTEGER NOT NULL DEFAULT 0,
          Error TEXT
        );
        CREATE INDEX IX_OperationEntry_Op ON OperationEntry(OperationId);
        """;
}
