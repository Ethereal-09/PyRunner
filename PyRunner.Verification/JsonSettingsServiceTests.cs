using System.Text.Json;
using PyRunner.Models;
using PyRunner.Services;

internal static class JsonSettingsServiceTests
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Verify(bool condition, string name)
        {
            if (condition) Console.WriteLine($"PASS: {name}");
            else
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {name}");
            }
        }

        var root = Path.Combine(Path.GetTempPath(), "PyRunner.SettingsVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var concurrentDirectory = Path.Combine(root, "concurrent");
            using (var settings = new JsonSettingsService(concurrentDirectory))
            {
                var updates = Enumerable.Range(0, 64)
                    .Select(_ => Task.Run(() => settings.Update(value => value.UiLayoutVersion++)));
                await Task.WhenAll(updates);
                settings.FlushOrThrow();
                Verify(settings.Current.UiLayoutVersion == 64,
                    "concurrent transactional settings updates preserve every mutation");

                var concurrentWrites = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
                {
                    settings.Update(value => value.UiLayoutVersion++);
                    settings.FlushOrThrow();
                }));
                await Task.WhenAll(concurrentWrites);
                var concurrentPersisted = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(settings.SettingsPath));
                Verify(settings.Current.UiLayoutVersion == 80 &&
                       concurrentPersisted?.UiLayoutVersion == 80 &&
                       !Directory.EnumerateFiles(concurrentDirectory, "*.tmp").Any(),
                    "concurrent settings writes are serialized and leave valid JSON without temporary files");

                var cache = new SettingsUpdateCacheStore(settings);
                await Task.WhenAll(
                    Task.Run(() => settings.Update(value => value.Theme = "Light")),
                    Task.Run(() => cache.SaveAttempt(DateTimeOffset.Parse("2026-09-08T08:00:00Z"))),
                    Task.Run(() => cache.SaveSuccessfulResult(new UpdateCheckCache(
                        "1.2.3",
                        DateTimeOffset.Parse("2026-09-01T08:00:00Z"),
                        "notes",
                        "https://github.com/Ethereal-09/PyRunner/releases/tag/v1.2.3",
                        "\"etag\"",
                        DateTimeOffset.Parse("2026-09-08T08:00:00Z")))));
                settings.FlushOrThrow();
                var persisted = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(settings.SettingsPath));
                Verify(persisted?.Theme == "Light" &&
                       persisted.LastUpdateCheckAttemptUtc is not null &&
                       persisted.LastSuccessfulUpdateCheck?.Version == "1.2.3",
                    "update cache and settings-page values survive concurrent saves");
            }

            var backupDirectory = Path.Combine(root, "backup");
            using (var settings = new JsonSettingsService(backupDirectory))
            {
                settings.Update(value => value.Theme = "Light");
                settings.FlushOrThrow();
                settings.Update(value => value.Theme = "Dark");
                settings.FlushOrThrow();
                Verify(File.Exists(settings.BackupPath),
                    "replacing settings preserves a recoverable backup");
            }
            File.WriteAllText(Path.Combine(backupDirectory, "settings.json"), "{broken-json");
            using (var recovered = new JsonSettingsService(backupDirectory))
            {
                Verify(recovered.Current.Theme == "Light" &&
                       Directory.EnumerateFiles(backupDirectory, "*.corrupt").Any() &&
                       File.Exists(recovered.SettingsPath) &&
                       File.Exists(Path.Combine(backupDirectory, "settings-diagnostics.log")),
                    "corrupt settings are isolated and the previous backup is restored");
            }

            var corruptDirectory = Path.Combine(root, "corrupt-without-backup");
            Directory.CreateDirectory(corruptDirectory);
            File.WriteAllText(Path.Combine(corruptDirectory, "settings.json"), "not-json");
            using (var recovered = new JsonSettingsService(corruptDirectory))
            {
                Verify(recovered.Current.Theme == "Dark" &&
                       Directory.EnumerateFiles(corruptDirectory, "*.corrupt").Any(),
                    "corrupt settings without a backup fall back to defaults without overwriting the corrupt file");
            }

            var legacyDirectory = Path.Combine(root, "legacy");
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllText(
                Path.Combine(legacyDirectory, "settings.json"),
                "{\"Language\":\"en-US\",\"TerminalAutoClear\":true}");
            using (var legacy = new JsonSettingsService(legacyDirectory))
            {
                Verify(legacy.Current.Language == "en-US" &&
                       legacy.Current.TerminalAutoClear &&
                       legacy.Current.AutoRefreshScripts,
                    "older settings files remain compatible and receive defaults for new fields");
            }

            var blocker = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blocker, "preserve-me");
            using (var failing = new JsonSettingsService(blocker))
            {
                failing.Update(value => value.Theme = "Light");
                var writeFailed = false;
                try { failing.FlushOrThrow(); }
                catch (IOException) { writeFailed = true; }
                Verify(writeFailed && File.ReadAllText(blocker) == "preserve-me",
                    "write failures do not overwrite an existing unrelated file");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        return failures;
    }
}
