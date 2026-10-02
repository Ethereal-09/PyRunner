using PyRunner.Data;
using PyRunner.Models;
using PyRunner.Services;

internal static class ScheduledTaskServiceTests
{
    public static int Run()
    {
        var failures = 0;
        var root = Path.Combine(Path.GetTempPath(), "PyRunner.ScheduleVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var factory = new SqliteConnectionFactory(root);
            var migrator = new SchemaMigrator(factory);
            migrator.Migrate();
            migrator.Migrate();

            using (var connection = factory.CreateOpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;";
                Check(Convert.ToInt32(command.ExecuteScalar()) == 5, "scheduled-task migration remains idempotent after diagnostics migration", ref failures);
            }

            var scriptPath = Path.Combine(root, "scheduled.py");
            File.WriteAllText(scriptPath, "print('scheduled')");
            var scripts = new ScriptService(factory);
            var script = new Script { Name = "Scheduled", FilePath = scriptPath };
            scripts.Add(script);

            var tasks = new ScheduledTaskService(factory);
            var task = new ScheduledTask
            {
                Name = "Scheduled daily",
                ScriptId = script.Id,
                ScriptName = script.Name,
                ScriptPath = script.FilePath,
                ScheduleType = ScheduleTypes.Daily,
                StartAtLocal = DateTime.Now.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:ss"),
                Enabled = true,
            };
            var taskId = tasks.Add(task);
            Check(taskId > 0 && tasks.GetById(taskId)?.NextRunAtUtc != null,
                "scheduled task persists its next run", ref failures);

            tasks.SetLastResult(taskId, "failed", "Run_WorkingDirectoryMissing");
            Check(tasks.GetById(taskId)?.LastErrorKey == "Run_WorkingDirectoryMissing",
                "scheduled-task failure reason is persisted", ref failures);
            tasks.SetLastResult(taskId, "success");
            Check(tasks.GetById(taskId)?.LastErrorKey is null,
                "successful schedule result clears the previous failure reason", ref failures);

            var records = new RunRecordService(factory);
            records.StartRun(script.Id);
            tasks.SetLastResult(taskId, "running");
            records.RecoverInterruptedRuns();
            records.RecoverInterruptedRuns();
            Check(records.GetRecent(script.Id).Single() is { Status: "failed", MetricsStatus: "interrupted", FinishedAt: not null }
                && tasks.GetById(taskId)?.LastErrorKey == "Run_Interrupted",
                "startup interruption recovery is idempotent for history and schedules", ref failures);

            using (var watcher = new ScriptDirectoryWatcher(new ScriptPathService(factory)))
            using (var changed = new AutoResetEvent(false))
            {
                watcher.Changed += () => changed.Set();
                watcher.SyncWithPaths(new[] { root });
                var folder = Path.Combine(root, "scripts.folder");
                Directory.CreateDirectory(folder);
                Check(changed.WaitOne(TimeSpan.FromSeconds(5)), "creating a plain directory refreshes the script tree", ref failures);
                var moved = Path.Combine(root, "renamed.folder");
                Directory.Move(folder, moved);
                Check(changed.WaitOne(TimeSpan.FromSeconds(5)), "renaming a directory refreshes the script tree", ref failures);
                Directory.Delete(moved);
                Check(changed.WaitOne(TimeSpan.FromSeconds(5)), "deleting a directory refreshes the script tree", ref failures);
            }

            scripts.Delete(script.Id);
            var detached = tasks.GetById(taskId);
            Check(detached is { ScriptId: null, Enabled: false, LastResult: "missing" },
                "deleting script preserves and disables its scheduled task", ref failures);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return failures;
    }

    private static void Check(bool condition, string name, ref int failures)
    {
        if (condition) Console.WriteLine($"PASS: {name}");
        else { failures++; Console.Error.WriteLine($"FAIL: {name}"); }
    }
}
