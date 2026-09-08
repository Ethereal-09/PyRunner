using Dapper;
using PyRunner.Data;
using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>脚本路径服务契约（PRD §8.4）。</summary>
public interface IScriptPathService
{
    IReadOnlyList<ScriptPath> GetAll();

    /// <summary>添加（目录必须存在且路径唯一，重复抛 ValidationException: Error_PathDuplicate）。</summary>
    ScriptPath Add(string path);

    void Remove(int id);

    /// <summary>扫描目录内的用户 .py 文件，跳过环境、依赖、缓存和目录链接。</summary>
    IReadOnlyList<string> Scan(string path);
}

/// <summary>脚本路径服务实现。</summary>
public sealed class ScriptPathService : IScriptPathService
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public ScriptPathService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public IReadOnlyList<ScriptPath> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        return connection.Query<ScriptPath>(
            "SELECT Id, Path, Enabled, CreatedAt FROM ScriptPath ORDER BY Path;")
            .ToList();
    }

    public ScriptPath Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            throw new ValidationException("Error_DirectoryNotFound");

        var full = Path.GetFullPath(path.Trim());
        using var connection = _connectionFactory.CreateOpenConnection();

        var duplicate = connection.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM ScriptPath WHERE Path = @Path COLLATE NOCASE;", new { Path = full });
        if (duplicate > 0)
            throw new ValidationException("Error_PathDuplicate");

        var now = TimeFormat.UtcNowIso();
        try
        {
            var id = connection.ExecuteScalar<long>(
                @"INSERT INTO ScriptPath (Path, Enabled, CreatedAt) VALUES (@Path, 1, @Now);
                  SELECT last_insert_rowid();",
                new { Path = full, Now = now });

            return new ScriptPath { Id = (int)id, Path = full, Enabled = true, CreatedAt = now };
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ScriptService.IsUniqueConstraintViolation(ex))
        {
            // DB NOCASE 唯一约束兜底（判重与插入之间的 TOCTOU 窗口）
            throw new ValidationException("Error_PathDuplicate");
        }
    }

    public void Remove(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        connection.Execute("DELETE FROM ScriptPath WHERE Id = @Id;", new { Id = id });
    }

    public IReadOnlyList<string> Scan(string path)
    {
        return ScriptDirectoryPolicy.ScanPythonFiles(path);
    }
}
