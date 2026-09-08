using Microsoft.Data.Sqlite;

namespace PyRunner.Data;

/// <summary>SQLite 启动故障恢复：尽力修复，或先完整备份再重建空库。</summary>
public sealed class DatabaseRecoveryService
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public DatabaseRecoveryService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>对可打开的数据库执行一致性检查、索引重建与压缩。</summary>
    public bool TryRepair()
    {
        using var connection = new SqliteConnection($"Data Source={_connectionFactory.DatabasePath}");
        connection.Open();

        if (IntegrityCheck(connection)) return true;

        using (var repair = connection.CreateCommand())
        {
            repair.CommandText = "REINDEX; VACUUM;";
            repair.ExecuteNonQuery();
        }

        return IntegrityCheck(connection);
    }

    /// <summary>
    /// 将数据库及 WAL/SHM 辅助文件复制到时间戳备份目录，再删除原文件供迁移器创建新库。
    /// 返回备份目录，便于后续人工恢复元数据。
    /// </summary>
    public string BackupAndReset()
    {
        // Dispose 后连接仍可能驻留在 Microsoft.Data.Sqlite 连接池并占用 WAL/SHM；
        // 文件级备份与替换前必须清池，保证三个固定数据文件可一致处理。
        SqliteConnection.ClearAllPools();

        var databasePath = _connectionFactory.DatabasePath;
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDirectory = Path.Combine(_connectionFactory.DatabaseDirectory, "Backups", timestamp);
        Directory.CreateDirectory(backupDirectory);

        foreach (var source in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (!File.Exists(source)) continue;
            File.Copy(source, Path.Combine(backupDirectory, Path.GetFileName(source)), overwrite: true);
        }

        // 目标均为固定的 PyRunner 数据文件，不涉及用户脚本目录。
        foreach (var target in new[] { databasePath + "-wal", databasePath + "-shm", databasePath })
        {
            if (File.Exists(target)) File.Delete(target);
        }

        _connectionFactory.ResetInitialization();
        return backupDirectory;
    }

    private static bool IntegrityCheck(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
