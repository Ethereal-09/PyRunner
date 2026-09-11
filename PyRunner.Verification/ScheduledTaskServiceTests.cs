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
                Check(Convert.ToInt32(command.ExecuteScalar()) == 2, "scheduled-task migration is idempotent", ref failures);
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
