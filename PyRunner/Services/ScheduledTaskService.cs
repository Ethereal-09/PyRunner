using Dapper;
using PyRunner.Data;
using PyRunner.Models;
using System.Globalization;

namespace PyRunner.Services;

public interface IScheduledTaskService
{
    IReadOnlyList<ScheduledTask> GetAll();
    ScheduledTask? GetById(int id);
    IReadOnlyList<ScheduledTask> GetDue(DateTimeOffset utcNow);
    int Add(ScheduledTask task);
    void Update(ScheduledTask task);
    void Delete(int id);
    void SetEnabled(int id, bool enabled);
    void AdvanceAfterTrigger(ScheduledTask task, DateTimeOffset utcNow);
    void SetLastResult(int id, string result);
}

public sealed class ScheduledTaskService : IScheduledTaskService
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public ScheduledTaskService(SqliteConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public IReadOnlyList<ScheduledTask> GetAll()
    {
        RepairDetachedScriptLinks();
        using var connection = _connectionFactory.CreateOpenConnection();
        return connection.Query<ScheduledTask>(SelectSql + " ORDER BY Enabled DESC, NextRunAtUtc, Id;").ToList();
    }

    public ScheduledTask? GetById(int id)
    {
        RepairDetachedScriptLinks();
        using var connection = _connectionFactory.CreateOpenConnection();
        return connection.QueryFirstOrDefault<ScheduledTask>(SelectSql + " WHERE Id=@Id;", new { Id = id });
    }

    public IReadOnlyList<ScheduledTask> GetDue(DateTimeOffset utcNow)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        return connection.Query<ScheduledTask>(SelectSql +
            " WHERE Enabled=1 AND NextRunAtUtc IS NOT NULL AND NextRunAtUtc<=@Now ORDER BY NextRunAtUtc, Id;",
            new { Now = ToIso(utcNow) }).ToList();
    }

    public int Add(ScheduledTask task)
    {
        Validate(task);
        var now = TimeFormat.UtcNowIso();
        task.NextRunAtUtc = ScheduleCalculator.GetNextUtc(task, DateTimeOffset.UtcNow)?.ToString("O", CultureInfo.InvariantCulture);
        if (task.Enabled && task.NextRunAtUtc == null) throw new ValidationException("Schedule_Error_TimeInvalid");
        using var connection = _connectionFactory.CreateOpenConnection();
        var id = connection.ExecuteScalar<long>(@"
            INSERT INTO ScheduledTask
            (Name,ScriptId,ScriptName,ScriptPath,ScheduleType,StartAtLocal,DaysOfWeek,IntervalMinutes,NextRunAtUtc,Enabled,CreatedAt,UpdatedAt)
            VALUES (@Name,@ScriptId,@ScriptName,@ScriptPath,@ScheduleType,@StartAtLocal,@DaysOfWeek,@IntervalMinutes,@NextRunAtUtc,@Enabled,@Now,@Now);
            SELECT last_insert_rowid();", new
        {
            task.Name, task.ScriptId, task.ScriptName, task.ScriptPath, task.ScheduleType,
            task.StartAtLocal, task.DaysOfWeek, task.IntervalMinutes, task.NextRunAtUtc, task.Enabled, Now = now,
        });
        task.Id = (int)id;
        return task.Id;
    }

    public void Update(ScheduledTask task)
    {
        Validate(task);
        task.NextRunAtUtc = task.Enabled
            ? ScheduleCalculator.GetNextUtc(task, DateTimeOffset.UtcNow)?.ToString("O", CultureInfo.InvariantCulture)
            : null;
        if (task.Enabled && task.NextRunAtUtc == null) throw new ValidationException("Schedule_Error_TimeInvalid");
        using var connection = _connectionFactory.CreateOpenConnection();
        connection.Execute(@"UPDATE ScheduledTask SET Name=@Name,ScriptId=@ScriptId,ScriptName=@ScriptName,
            ScriptPath=@ScriptPath,ScheduleType=@ScheduleType,StartAtLocal=@StartAtLocal,DaysOfWeek=@DaysOfWeek,
            IntervalMinutes=@IntervalMinutes,NextRunAtUtc=@NextRunAtUtc,Enabled=@Enabled,UpdatedAt=@Now WHERE Id=@Id;",
            new
            {
                task.Id, task.Name, task.ScriptId, task.ScriptName, task.ScriptPath, task.ScheduleType,
                task.StartAtLocal, task.DaysOfWeek, task.IntervalMinutes, task.NextRunAtUtc, task.Enabled,
                Now = TimeFormat.UtcNowIso(),
            });
    }

    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        connection.Execute("DELETE FROM ScheduledTask WHERE Id=@Id;", new { Id = id });
    }

    public void SetEnabled(int id, bool enabled)
    {
        var task = GetById(id);
        if (task == null) return;
        task.Enabled = enabled;
        Update(task);
    }

    public void AdvanceAfterTrigger(ScheduledTask task, DateTimeOffset utcNow)
    {
        var isOnce = task.ScheduleType == ScheduleTypes.Once;
        var next = isOnce ? null : ScheduleCalculator.GetNextUtc(task, utcNow.AddMilliseconds(1));
        using var connection = _connectionFactory.CreateOpenConnection();
        connection.Execute(@"UPDATE ScheduledTask SET LastRunAtUtc=@Now,LastResult='running',
            NextRunAtUtc=@Next,Enabled=@Enabled,UpdatedAt=@Now WHERE Id=@Id;", new
        {
            task.Id,
            Now = ToIso(utcNow),
            Next = next?.ToString("O", CultureInfo.InvariantCulture),
            Enabled = !isOnce,
        });
    }

    public void SetLastResult(int id, string result)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        connection.Execute("UPDATE ScheduledTask SET LastResult=@Result,UpdatedAt=@Now WHERE Id=@Id;",
            new { Id = id, Result = result, Now = TimeFormat.UtcNowIso() });
    }

    private void RepairDetachedScriptLinks()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        connection.Execute(@"UPDATE ScheduledTask SET ScriptId=(
              SELECT s.Id FROM Script s WHERE s.FilePath=ScheduledTask.ScriptPath COLLATE NOCASE LIMIT 1)
            WHERE ScriptId IS NULL AND EXISTS(
              SELECT 1 FROM Script s WHERE s.FilePath=ScheduledTask.ScriptPath COLLATE NOCASE); ");
        connection.Execute("UPDATE ScheduledTask SET Enabled=0,NextRunAtUtc=NULL,LastResult='missing' WHERE ScriptId IS NULL AND Enabled=1;");
    }

    private static void Validate(ScheduledTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Name)) throw new ValidationException("Schedule_Error_NameRequired");
        if (task.ScriptId == null || string.IsNullOrWhiteSpace(task.ScriptPath))
            throw new ValidationException("Schedule_Error_ScriptRequired");
        if (!DateTime.TryParse(task.StartAtLocal, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ValidationException("Schedule_Error_TimeInvalid");
        if (task.ScheduleType == ScheduleTypes.Weekly && task.DaysOfWeek == 0)
            throw new ValidationException("Schedule_Error_WeekdayRequired");
        if (task.ScheduleType == ScheduleTypes.Interval && task.IntervalMinutes < 1)
            throw new ValidationException("Schedule_Error_IntervalInvalid");
    }

    private static string ToIso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private const string SelectSql = @"SELECT Id,Name,ScriptId,ScriptName,ScriptPath,ScheduleType,StartAtLocal,
        DaysOfWeek,IntervalMinutes,NextRunAtUtc,LastRunAtUtc,LastResult,Enabled,CreatedAt,UpdatedAt FROM ScheduledTask";
}

public static class ScheduleCalculator
{
    public static DateTimeOffset? GetNextUtc(ScheduledTask task, DateTimeOffset afterUtc)
    {
        if (!DateTime.TryParse(task.StartAtLocal, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var configured)) return null;

        configured = DateTime.SpecifyKind(configured, DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.Local;
        var afterLocal = TimeZoneInfo.ConvertTime(afterUtc, zone).DateTime;
        DateTime? candidate = task.ScheduleType switch
        {
            ScheduleTypes.Once => configured > afterLocal ? configured : null,
            ScheduleTypes.Daily => NextDaily(configured, afterLocal),
            ScheduleTypes.Weekly => NextWeekly(configured, task.DaysOfWeek, afterLocal),
            ScheduleTypes.Interval => NextInterval(configured, task.IntervalMinutes, afterLocal),
            _ => null,
        };
        return candidate == null ? null : ToUtc(candidate.Value, zone);
    }

    private static DateTime NextDaily(DateTime configured, DateTime after)
    {
        var value = after.Date + configured.TimeOfDay;
        return value > after ? value : value.AddDays(1);
    }

    private static DateTime? NextWeekly(DateTime configured, int mask, DateTime after)
    {
        for (var offset = 0; offset <= 7; offset++)
        {
            var value = after.Date.AddDays(offset) + configured.TimeOfDay;
            if (value <= after) continue;
            if ((mask & (1 << (int)value.DayOfWeek)) != 0) return value;
        }
        return null;
    }

    private static DateTime NextInterval(DateTime configured, int minutes, DateTime after)
    {
        if (configured > after) return configured;
        var intervalTicks = TimeSpan.FromMinutes(Math.Max(1, minutes)).Ticks;
        var steps = ((after.Ticks - configured.Ticks) / intervalTicks) + 1;
        return configured.AddTicks(steps * intervalTicks);
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
