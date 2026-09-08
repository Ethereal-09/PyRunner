using System.Reflection;
using System.Text.RegularExpressions;

namespace PyRunner.Data;

/// <summary>
/// 架构迁移器：启动时比较 SchemaVersion，按编号顺序执行嵌入的迁移脚本
/// （Data/Migrations/NNN_*.sql），每个迁移事务包裹、整体幂等、仅 ADD 不 DROP。
/// </summary>
public sealed class SchemaMigrator
{
    private const string MigrationResourcePrefix = "PyRunner.Data.Migrations.";

    private readonly SqliteConnectionFactory _connectionFactory;

    public SchemaMigrator(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>执行全部未应用的迁移。幂等：二次启动不再重复建表。</summary>
    public void Migrate()
    {
        using var connection = _connectionFactory.CreateOpenConnection();

        // 版本表本身可能在首个迁移中创建；此处先确保存在以便读取当前版本
        using (var ensureCmd = connection.CreateCommand())
        {
            ensureCmd.CommandText =
                "CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER PRIMARY KEY, AppliedAt TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')));";
            ensureCmd.ExecuteNonQuery();
        }

        var currentVersion = 0;
        using (var versionCmd = connection.CreateCommand())
        {
            versionCmd.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;";
            var result = versionCmd.ExecuteScalar();
            currentVersion = Convert.ToInt32(result);
        }

        var migrations = DiscoverMigrations()
            .Where(m => m.Version > currentVersion)
            .OrderBy(m => m.Version)
            .ToList();

        foreach (var migration in migrations)
        {
            ApplyMigration(connection, migration);
#if DEBUG
            DebugLog.WriteLine($"SchemaMigrator: 已应用迁移 {migration.Name}（{currentVersion} → {migration.Version}）");
#endif
            currentVersion = migration.Version;
        }

        if (migrations.Count == 0)
        {
#if DEBUG
            DebugLog.WriteLine($"SchemaMigrator: 无待应用迁移，当前 SchemaVersion={currentVersion}");
#endif
        }
    }

    /// <summary>从程序集嵌入资源发现全部迁移脚本（NNN_前缀解析版本号）。</summary>
    private static List<(int Version, string Name, string ResourceName)> DiscoverMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var result = new List<(int, string, string)>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(MigrationResourcePrefix, StringComparison.Ordinal)) continue;
            if (!resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) continue;

            var fileName = resourceName[MigrationResourcePrefix.Length..];
            var match = Regex.Match(fileName, @"^(\d+)_");
            if (!match.Success) continue;

            result.Add((int.Parse(match.Groups[1].Value), fileName, resourceName));
        }

        return result;
    }

    /// <summary>事务内应用单个迁移；PRAGMA 语句在事务外单独执行（SQLite 限制）。</summary>
    private static void ApplyMigration(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        (int Version, string Name, string ResourceName) migration)
    {
        var sql = ReadEmbeddedResource(migration.ResourceName);

        var pragmaStatements = new List<string>();
        var bodySql = StripPragmas(sql, pragmaStatements);

        foreach (var pragma in pragmaStatements)
        {
            using var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = pragma;
            pragmaCmd.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            using (var bodyCmd = connection.CreateCommand())
            {
                bodyCmd.Transaction = transaction;
                bodyCmd.CommandText = bodySql;
                bodyCmd.ExecuteNonQuery();
            }

            using (var versionCmd = connection.CreateCommand())
            {
                versionCmd.Transaction = transaction;
                versionCmd.CommandText = "INSERT OR IGNORE INTO SchemaVersion (Version) VALUES ($version);";
                versionCmd.Parameters.AddWithValue("$version", migration.Version);
                versionCmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            // 回滚失败不得掩盖原始迁移异常：Rollback 自身包 try/catch 后重抛原异常
            try { transaction.Rollback(); }
            catch { /* 回滚失败忽略，原始异常才是根因 */ }
            throw;
        }
    }

    /// <summary>剥离以 PRAGMA 开头的语句行（收集到 pragmas 列表），其余原样返回。</summary>
    private static string StripPragmas(string sql, List<string> pragmas)
    {
        var lines = sql.Split('\n');
        var body = new System.Text.StringBuilder();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase))
            {
                var statement = line.Trim().TrimEnd(';');
                if (statement.Length > 0) pragmas.Add(statement + ";");
                continue;
            }
            body.AppendLine(line);
        }

        return body.ToString();
    }

    private static string ReadEmbeddedResource(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"嵌入迁移资源不存在: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
