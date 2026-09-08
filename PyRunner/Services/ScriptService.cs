using Dapper;
using Microsoft.Data.Sqlite;
using PyRunner.Data;
using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>脚本登记服务实现（SQLite + Dapper，VM 不直接接触 SQL）。</summary>
public sealed class ScriptService : IScriptService
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public ScriptService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public IReadOnlyList<Script> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        return connection.Query<Script>(
            "SELECT Id, Name, FilePath, Category, Tags, Description, InterpreterId, Arguments, WorkingDirectory, IsFavorite, CreatedAt, UpdatedAt FROM Script ORDER BY Name;")
            .ToList();
    }

    public Script? GetById(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        return connection.QueryFirstOrDefault<Script>(
            "SELECT Id, Name, FilePath, Category, Tags, Description, InterpreterId, Arguments, WorkingDirectory, IsFavorite, CreatedAt, UpdatedAt FROM Script WHERE Id = @Id;",
            new { Id = id });
    }

    public int Add(Script script)
    {
        ValidateForInsert(script);

        var now = TimeFormat.UtcNowIso();
        using var connection = _connectionFactory.CreateOpenConnection();

        try
        {
            // Category 为空 → 省略列值，由 DB DEFAULT '未分类' 兜底（PRD §7.1）
            var id = connection.ExecuteScalar<long>(
                @"INSERT INTO Script (Name, FilePath, Category, Tags, Description, InterpreterId, Arguments, WorkingDirectory, IsFavorite, CreatedAt, UpdatedAt)
                  VALUES (@Name, @FilePath, COALESCE(@Category, '未分类'), @Tags, @Description, @InterpreterId, @Arguments, @WorkingDirectory, @IsFavorite, @Now, @Now);
                  SELECT last_insert_rowid();",
                new
                {
                    script.Name,
                    script.FilePath,
                    Category = NullIfBlank(script.Category),
                    Tags = NullIfBlank(script.Tags),
                    Description = NullIfBlank(script.Description),
                    script.InterpreterId,
                    Arguments = NullIfBlank(script.Arguments),
                    WorkingDirectory = NullIfBlank(script.WorkingDirectory),
                    script.IsFavorite,
                    Now = now,
                });

            script.Id = (int)id;
            return script.Id;
        }
        catch (SqliteException ex) when (IsUniqueConstraintViolation(ex))
        {
            // 内存查重与 INSERT 之间存在 TOCTOU 窗口：DB 约束兜底，映射为业务校验异常
            throw new ValidationException("Error_PathDuplicate");
        }
    }

    public void Update(Script script)
    {
        if (string.IsNullOrWhiteSpace(script.Name))
            throw new InvalidOperationException("Error_NameRequired");
        ValidatePathExists(script.FilePath);
        EnsurePathUnique(script.FilePath, excludeId: script.Id);

        var now = TimeFormat.UtcNowIso();
        using var connection = _connectionFactory.CreateOpenConnection();

        try
        {
            connection.Execute(
                @"UPDATE Script SET Name=@Name, FilePath=@FilePath, Category=COALESCE(@Category,'未分类'),
                  Tags=@Tags, Description=@Description, InterpreterId=@InterpreterId, Arguments=@Arguments,
                  WorkingDirectory=@WorkingDirectory, UpdatedAt=@Now WHERE Id=@Id;",
                new
                {
                    script.Id,
                    script.Name,
                    script.FilePath,
                    Category = NullIfBlank(script.Category),
                    Tags = NullIfBlank(script.Tags),
                    Description = NullIfBlank(script.Description),
                    script.InterpreterId,
                    Arguments = NullIfBlank(script.Arguments),
                    WorkingDirectory = NullIfBlank(script.WorkingDirectory),
                    Now = now,
                });
        }
        catch (SqliteException ex) when (IsUniqueConstraintViolation(ex))
        {
            throw new ValidationException("Error_PathDuplicate");
        }
    }

    public void Delete(int id)
    {
        // 仅删除元数据；RunRecord 经 FK ON DELETE CASCADE 级联清理。不触碰磁盘文件。
        using var connection = _connectionFactory.CreateOpenConnection();
        connection.Execute("DELETE FROM Script WHERE Id = @Id;", new { Id = id });
    }

    public IReadOnlyList<Script> Search(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return GetAll();

        return GetAll()
            .Where(s =>
                Contains(s.Name, keyword) ||
                Contains(s.Description, keyword) ||
                Contains(s.Tags, keyword))
            .ToList();
    }

    public void ToggleFavorite(int id, bool isFavorite)
    {
        var now = TimeFormat.UtcNowIso();
        using var connection = _connectionFactory.CreateOpenConnection();
        connection.Execute(
            "UPDATE Script SET IsFavorite=@IsFavorite, UpdatedAt=@Now WHERE Id=@Id;",
            new { Id = id, IsFavorite = isFavorite, Now = now });
    }

    public int ImportFromPaths(IReadOnlyList<string> directoryPaths)
    {
        var discoveredPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directoryPaths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            foreach (var filePath in ScriptDirectoryPolicy.ScanPythonFiles(directory))
                discoveredPaths.Add(filePath);
        }

        var existing = GetAll();
        var changes = 0;

        // Reconcile stale records from removed roots, deleted files, or newly excluded
        // environments. Delete() only removes metadata; the disk file is never touched.
        foreach (var script in existing.Where(script => !discoveredPaths.Contains(script.FilePath)))
        {
            Delete(script.Id);
            changes++;
        }

        var knownPaths = new HashSet<string>(
            existing.Where(script => discoveredPaths.Contains(script.FilePath)).Select(script => script.FilePath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in discoveredPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            // 路径唯一去重：已登记（含本轮先导入的）直接跳过
            if (!knownPaths.Add(filePath)) continue;

            try
            {
                Add(new Script
                {
                    Name = Path.GetFileNameWithoutExtension(filePath),
                    FilePath = filePath,
                    Category = Path.GetFileName(Path.GetDirectoryName(filePath)),
                });
                changes++;
            }
            catch (ValidationException)
            {
                // 单个文件登记失败（如 DB 约束 TOCTOU 兜底）：跳过不中断批量导入
                knownPaths.Remove(filePath);
            }
        }

        return changes;
    }

    // ---- 校验 ----

    private void ValidateForInsert(Script script)
    {
        if (string.IsNullOrWhiteSpace(script.Name))
            throw new ValidationException("Error_NameRequired");
        ValidatePathExists(script.FilePath);
        EnsurePathUnique(script.FilePath, excludeId: null);
    }

    /// <summary>服务层兜底校验：文件必须存在且为 .py（不依赖对话框层校验）。</summary>
    private static void ValidatePathExists(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new ValidationException("Error_PathNotFound");
        if (!filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("Error_PathNotPy");
    }

    private void EnsurePathUnique(string filePath, int? excludeId)
    {
        // 内存 OrdinalIgnoreCase 比较（Windows 路径大小写不敏感，DB 层另有 NOCASE 约束兜底）
        var duplicate = GetAll().Any(s =>
            string.Equals(s.FilePath, filePath, StringComparison.OrdinalIgnoreCase) &&
            (excludeId == null || s.Id != excludeId.Value));

        if (duplicate)
            throw new ValidationException("Error_PathDuplicate");
    }

    /// <summary>SQLite 唯一约束冲突判定（SQLITE_CONSTRAINT=19，派生码 UNIQUE=2067/PRIMARY=1555）。</summary>
    internal static bool IsUniqueConstraintViolation(SqliteException ex) =>
        ex.SqliteErrorCode == 19 /* SQLITE_CONSTRAINT */ &&
        (ex.SqliteExtendedErrorCode == 2067 /* SQLITE_CONSTRAINT_UNIQUE */ ||
         ex.SqliteExtendedErrorCode == 1555 /* SQLITE_CONSTRAINT_PRIMARYKEY */);

    private static bool Contains(string? source, string keyword) =>
        source != null && source.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>时间戳统一格式：UTC ISO-8601，毫秒精度（与迁移 SQL 的 strftime '%f' 一致；
/// 秒精度会导致同一秒内的多条记录排序不稳定）。展示层转本地时间。</summary>
internal static class TimeFormat
{
    public static string UtcNowIso() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
