using Dapper;
using PyRunner.Data;
using PyRunner.Models;

namespace PyRunner.Services;

/// <summary>运行记录服务契约（PRD §8 服务层补充：开始/结束记录、保留策略、输出截断）。</summary>
public interface IRunRecordService
{
    /// <summary>开始运行：插入 status='running' 记录并顺带清理超出保留数的旧记录，返回记录 Id。</summary>
    int StartRun(int scriptId);

    /// <summary>结束运行：写入结束时间/退出码/状态/输出（200KB 尾部截断后文本）。</summary>
    void FinishRun(int recordId, int? exitCode, RunStatus finalStatus, string? output);

    /// <summary>某脚本最近 N 条记录（默认 10，按开始时间倒序）。</summary>
    IReadOnlyList<RunRecord> GetRecent(int scriptId, int count = 10);
}

/// <summary>运行记录服务实现。</summary>
public sealed class RunRecordService : IRunRecordService
{
    /// <summary>每脚本保留的最近记录数。</summary>
    private const int RetentionPerScript = 10;

    /// <summary>FinishRun 落库输出的最大字符数（200KB 字符，服务端强制尾部截断，不依赖调用方纪律）。</summary>
    private const int MaxOutputChars = 200 * 1024;

    private readonly SqliteConnectionFactory _connectionFactory;

    public RunRecordService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public int StartRun(int scriptId)
    {
        var now = TimeFormat.UtcNowIso();
        using var connection = _connectionFactory.CreateOpenConnection();

        // INSERT + Prune 包事务：要么都生效，要么都不生效，避免清理后插入失败留下空洞
        using var transaction = connection.BeginTransaction();

        var id = connection.ExecuteScalar<long>(
            @"INSERT INTO RunRecord (ScriptId, StartedAt, Status) VALUES (@ScriptId, @Now, 'running');
              SELECT last_insert_rowid();",
            new { ScriptId = scriptId, Now = now }, transaction: transaction);

        PruneOldRecords(connection, scriptId, transaction);
        transaction.Commit();
        return (int)id;
    }

    public void FinishRun(int recordId, int? exitCode, RunStatus finalStatus, string? output)
    {
        if (finalStatus == RunStatus.NotRun || finalStatus == RunStatus.Running)
            throw new ArgumentException("结束状态必须为 Success/Failed/Killed", nameof(finalStatus));

        // 服务端强制兜底：超过 200KB 字符保留尾部（错误信息多在末尾）
        if (output != null && output.Length > MaxOutputChars)
            output = output.Substring(output.Length - MaxOutputChars);

        var now = TimeFormat.UtcNowIso();
        using var connection = _connectionFactory.CreateOpenConnection();
        var affected = connection.Execute(
            "UPDATE RunRecord SET FinishedAt=@Now, ExitCode=@ExitCode, Status=@Status, Output=@Output WHERE Id=@Id;",
            new
            {
                Id = recordId,
                Now = now,
                ExitCode = exitCode,
                Status = RunStatusMapping.ToDbString(finalStatus),
                Output = output,
            });

        if (affected == 0)
        {
            // 记录不存在（可能已被 Prune 清理或 Id 错误）：仅记日志，不抛异常打断退出流程
            System.Diagnostics.Debug.WriteLine(
                $"RunRecordService.FinishRun: 记录 Id={recordId} 不存在，UPDATE 影响 0 行");
        }
    }

    public IReadOnlyList<RunRecord> GetRecent(int scriptId, int count = 10)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        return connection.Query<RunRecord>(
            @"SELECT Id, ScriptId, StartedAt, FinishedAt, ExitCode, Status, Output FROM RunRecord
              WHERE ScriptId = @ScriptId ORDER BY StartedAt DESC, Id DESC LIMIT @Count;",
            new { ScriptId = scriptId, Count = count })
            .ToList();
    }

    /// <summary>写入时顺带清理：每脚本仅保留最近 RetentionPerScript 条；
    /// 仍处 running 的记录不清理（避免误删并发进行中的运行）。</summary>
    private void PruneOldRecords(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        int scriptId,
        Microsoft.Data.Sqlite.SqliteTransaction transaction)
    {
        connection.Execute(
            @"DELETE FROM RunRecord WHERE ScriptId = @ScriptId AND Status <> 'running' AND Id NOT IN (
                SELECT Id FROM RunRecord WHERE ScriptId = @ScriptId ORDER BY StartedAt DESC, Id DESC LIMIT @Keep);",
            new { ScriptId = scriptId, Keep = RetentionPerScript }, transaction: transaction);
    }
}

/// <summary>
/// 输出尾部环形缓冲：按字节累积、只保留最后 200KB（错误信息多在末尾），
/// 完成时按 UTF-8 解码（跳过缓冲截断产生的不完整首字节序列）。
/// </summary>
public sealed class TailOutputBuffer
{
    public const int MaxBytes = 200 * 1024;

    private readonly byte[] _buffer = new byte[MaxBytes];
    private readonly object _lock = new();
    private long _totalWritten;

    /// <summary>追加一段原始输出字节（线程安全）。</summary>
    public void Append(byte[] data, int offset, int count)
    {
        if (data == null || count <= 0) return;

        lock (_lock)
        {
            // 超过容量时只保留最后 MaxBytes 字节，但仍按环形位置写入，
            // 保证后续 Append / GetText 的游标语义一致
            var start = (int)(_totalWritten % MaxBytes);

            if (count >= MaxBytes)
            {
                var srcOffset = offset + count - MaxBytes;
                // 首个保留字节的环形落位 = 其绝对偏移 % 容量，保证与逐字节写入的状态完全一致
                var firstPos = (int)((_totalWritten + count - MaxBytes) % MaxBytes);
                var firstChunk = Math.Min(MaxBytes, MaxBytes - firstPos);
                Array.Copy(data, srcOffset, _buffer, firstPos, firstChunk);
                if (firstChunk < MaxBytes)
                    Array.Copy(data, srcOffset + firstChunk, _buffer, 0, MaxBytes - firstChunk);
                _totalWritten += count;
                return;
            }

            var chunk = Math.Min(count, MaxBytes - start);
            Array.Copy(data, offset, _buffer, start, chunk);
            if (chunk < count)
                Array.Copy(data, offset + chunk, _buffer, 0, count - chunk);

            _totalWritten += count;
        }
    }

    /// <summary>已累积字节总数（可能大于容量）。</summary>
    public long TotalWritten
    {
        get { lock (_lock) { return _totalWritten; } }
    }

    /// <summary>取出保留的尾部字节并 UTF-8 解码为文本。</summary>
    public string GetText()
    {
        byte[] snapshot;
        lock (_lock)
        {
            var length = (int)Math.Min(_totalWritten, MaxBytes);
            snapshot = new byte[length];

            if (_totalWritten <= MaxBytes)
            {
                Array.Copy(_buffer, 0, snapshot, 0, length);
            }
            else
            {
                var start = (int)(_totalWritten % MaxBytes); // 最旧字节的环形位置
                var firstChunk = MaxBytes - start;
                Array.Copy(_buffer, start, snapshot, 0, firstChunk);
                Array.Copy(_buffer, 0, snapshot, firstChunk, start);
            }
        }

        // 头部：截断可能落在多字节 UTF-8 序列中间，跳过开头最多 3 个续字节（10xxxxxx）
        var skip = 0;
        while (skip < Math.Min(3, snapshot.Length) && (snapshot[skip] & 0xC0) == 0x80)
            skip++;

        // 尾部：环形重排或流被中断时末尾可能残留不完整序列，丢弃未闭合的末字符
        var end = snapshot.Length;
        if (end > skip)
        {
            var i = end - 1;
            var continuation = 0;
            while (i >= Math.Max(skip, end - 3) && (snapshot[i] & 0xC0) == 0x80)
            {
                i--;
                continuation++;
            }

            if (i >= skip && snapshot[i] >= 0xC0)
            {
                // 首字节声明的续字节数：110x=1、1110x=2、11110x=3
                var expected = snapshot[i] >= 0xF0 ? 3 : snapshot[i] >= 0xE0 ? 2 : 1;
                if (continuation < expected) end = i;
            }
        }

        return System.Text.Encoding.UTF8.GetString(snapshot, skip, end - skip);
    }
}
