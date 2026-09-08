using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using PyRunner.Data;
using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>解释器服务实现（--version 探测、默认切换、引用保护）。</summary>
public sealed class InterpreterService : IInterpreterService
{
    private static readonly Regex VersionRegex = new(@"Python\s+(\d+\.\d+[0-9.]*)", RegexOptions.Compiled);

    private readonly SqliteConnectionFactory _connectionFactory;

    public InterpreterService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public IReadOnlyList<Interpreter> GetAll()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        return connection.Query<Interpreter>(
            "SELECT Id, Name, ExecutablePath, Version, IsDefault, CreatedAt FROM Interpreter ORDER BY IsDefault DESC, Name;")
            .ToList();
    }

    public Interpreter? GetDefault()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        return connection.QueryFirstOrDefault<Interpreter>(
            "SELECT Id, Name, ExecutablePath, Version, IsDefault, CreatedAt FROM Interpreter WHERE IsDefault = 1 LIMIT 1;");
    }

    public Interpreter AddFromPath(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            throw new ValidationException("Error_InterpreterNotFound");

        var fileName = Path.GetFileName(exePath);
        if (fileName.Equals("pythonw.exe", StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("Error_InterpreterIsPythonw");

        var version = DetectVersion(exePath);
        var name = version.Length > 0 ? $"Python {version}" : fileName;
        var now = TimeFormat.UtcNowIso();

        using var connection = _connectionFactory.CreateOpenConnection();

        // 判重 + 插入 + 首个置默认包进同一事务，消除并发下的双默认/TOCTOU 窗口
        using var transaction = connection.BeginTransaction();
        try
        {
            var existing = connection.QueryFirstOrDefault<Interpreter>(
                "SELECT Id, Name, ExecutablePath, Version, IsDefault, CreatedAt FROM Interpreter WHERE ExecutablePath = @Path COLLATE NOCASE;",
                new { Path = exePath }, transaction: transaction);

            if (existing != null)
            {
                // 已登记：刷新版本信息
                connection.Execute(
                    "UPDATE Interpreter SET Name=@Name, Version=@Version WHERE Id=@Id;",
                    new { Name = name, Version = version, Id = existing.Id }, transaction: transaction);
                transaction.Commit();
                existing.Name = name;
                existing.Version = version;
                return existing;
            }

            var isFirst = connection.ExecuteScalar<long>(
                "SELECT COUNT(1) FROM Interpreter;", transaction: transaction) == 0;

            var id = connection.ExecuteScalar<long>(
                @"INSERT INTO Interpreter (Name, ExecutablePath, Version, IsDefault, CreatedAt)
                  VALUES (@Name, @Path, @Version, @IsDefault, @Now);
                  SELECT last_insert_rowid();",
                new { Name = name, Path = exePath, Version = version, IsDefault = isFirst, Now = now },
                transaction: transaction);

            transaction.Commit();

            return new Interpreter
            {
                Id = (int)id,
                Name = name,
                ExecutablePath = exePath,
                Version = version,
                IsDefault = isFirst,
                CreatedAt = now,
            };
        }
        catch (SqliteException ex) when (ScriptService.IsUniqueConstraintViolation(ex))
        {
            // DB NOCASE 唯一约束兜底（判重与插入之间的 TOCTOU 窗口）
            transaction.Rollback();
            throw new ValidationException("Error_PathDuplicate");
        }
    }

    public string DetectVersion(string exePath)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            // 事件式异步读取：避免大输出时管道满导致 WaitForExit 死锁
            if (!process.Start()) return string.Empty;
            var output = ProcessOutputCollector.Attach(process);

            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
                return string.Empty;
            }
            process.WaitForExit(); // 确保异步读取线程排空缓冲

            // 不同 Python 版本可能把版本写到 stdout 或 stderr
            var match = VersionRegex.Match(output.GetText());
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public void SetDefault(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var transaction = connection.BeginTransaction();

        connection.Execute("UPDATE Interpreter SET IsDefault = 0;", transaction: transaction);
        var affected = connection.Execute(
            "UPDATE Interpreter SET IsDefault = 1 WHERE Id = @Id;", new { Id = id }, transaction: transaction);

        if (affected == 0)
        {
            transaction.Rollback();
            throw new ValidationException("Error_InterpreterNotFound");
        }

        transaction.Commit();
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();

        var referenced = connection.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM Script WHERE InterpreterId = @Id;", new { Id = id });
        if (referenced > 0)
            throw new ValidationException("Error_InterpreterInUse");

        connection.Execute("DELETE FROM Interpreter WHERE Id = @Id;", new { Id = id });
    }

    public IReadOnlyList<string> ScanCandidates() => PythonDiscovery.ScanCandidates();
}

/// <summary>
/// 进程输出事件式收集器：BeginOutputReadLine/BeginErrorReadLine 后再 WaitForExit，
/// 消除同步 ReadToEnd + WaitForExit 在大输出下的管道满死锁模式。
/// </summary>
internal sealed class ProcessOutputCollector
{
    private readonly StringBuilder _output = new();
    private readonly StringBuilder _error = new();

    private ProcessOutputCollector() { }

    /// <summary>挂接到进程并启动异步读取（调用方需在 process.Start() 之后调用）。</summary>
    public static ProcessOutputCollector Attach(Process process)
    {
        var collector = new ProcessOutputCollector();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (collector._output) collector._output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (collector._error) collector._error.AppendLine(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return collector;
    }

    /// <summary>合并 stdout/stderr 文本（以空格分隔）。</summary>
    public string GetText()
    {
        lock (_output)
        lock (_error)
            return _output.ToString() + " " + _error.ToString();
    }
}
