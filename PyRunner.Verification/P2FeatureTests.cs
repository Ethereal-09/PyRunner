using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PyRunner.Data;
using PyRunner.Editor;
using PyRunner.Models;
using PyRunner.Services;

internal static class P2FeatureTests
{
    public static async Task<int> RunAsync(string sourceRoot)
    {
        var failures = 0;
        void Verify(bool condition, string name)
        {
            if (condition) Console.WriteLine($"PASS: {name}");
            else { failures++; Console.Error.WriteLine($"FAIL: {name}"); }
        }

        var root = Path.Combine(Path.GetTempPath(), "PyRunner.P2Verification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scripts = Path.Combine(root, "scripts");
            Directory.CreateDirectory(scripts);
            var file = Path.Combine(scripts, "encoded.py");
            var original = new UTF8Encoding(true).GetPreamble()
                .Concat(new UTF8Encoding(false).GetBytes("# coding: utf-8\r\nprint('old')\r\n")).ToArray();
            await File.WriteAllBytesAsync(file, original);

            var documents = new CodeDocumentService();
            var loaded = await documents.LoadAsync(file);
            Verify(loaded.HasBom && loaded.NewLine == "\r\n" && loaded.Text.Contains("\n"),
                "code editor detects UTF-8 BOM and CRLF");
            var saved = await documents.SaveAsync(loaded, loaded.Text.Replace("old", "new"), false);
            var savedBytes = await File.ReadAllBytesAsync(file);
            Verify(saved.Status == CodeDocumentSaveStatus.Saved &&
                   savedBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()) &&
                   Encoding.UTF8.GetString(savedBytes).Contains("\r\n"),
                "safe save preserves BOM and newline convention");

            loaded = (await documents.LoadAsync(file));
            await File.AppendAllTextAsync(file, "# external\r\n");
            var conflict = await documents.SaveAsync(loaded, loaded.Text + "# editor\n", false);
            Verify(conflict.Status == CodeDocumentSaveStatus.Conflict &&
                   File.ReadAllText(file).Contains("external"),
                "external modification blocks silent overwrite");

            var existing = Path.Combine(scripts, "existing.py");
            await File.WriteAllTextAsync(existing, "sentinel");
            var saveAs = await documents.SaveAsAsync(loaded, existing, "replacement");
            Verify(saveAs.Status == CodeDocumentSaveStatus.Conflict && File.ReadAllText(existing) == "sentinel",
                "Save As never overwrites an existing file");

            var binary = Path.Combine(scripts, "binary.py");
            await File.WriteAllBytesAsync(binary, [1, 0, 2]);
            var binaryRejected = false;
            try { await documents.LoadAsync(binary); } catch (IOException) { binaryRejected = true; }
            Verify(binaryRejected, "code editor rejects NUL-containing files");

            var large = Path.Combine(scripts, "large.py");
            await File.WriteAllBytesAsync(large, Enumerable.Repeat((byte)'x', (int)CodeDocumentService.EditableSizeLimit + 1).ToArray());
            Verify((await documents.LoadAsync(large)).IsReadOnly, "files over 2 MiB open read-only");
            Verify(!Directory.EnumerateFiles(scripts, ".pyrunner-*.tmp").Any(),
                "editor operations leave no temporary files");

            var factory = new SqliteConnectionFactory(Path.Combine(root, "data"));
            new SchemaMigrator(factory).Migrate();
            var paths = new ScriptPathService(factory);
            paths.Add(scripts);
            var scriptService = new ScriptService(factory);
            var scriptId = scriptService.Add(new Script { Name = "encoded", FilePath = file });
            var records = new RunRecordService(factory);
            var recordId = records.StartRun(scriptId);
            records.FinishRun(recordId, 0, RunStatus.Success, "ok", 1234, 8UL * 1024 * 1024, "complete");
            var record = records.GetRecent(scriptId, 1).Single();
            Verify(record.DurationMs == 1234 && record.PeakJobMemoryBytes == 8L * 1024 * 1024 &&
                   record.MetricsStatus == "complete",
                "run duration and peak Job commit metrics round-trip through SQLite");
            var templates = new ScriptTemplateService(paths);
            Verify(!templates.Create("basic", scripts, "..\\escape.py").Succeeded &&
                   !templates.Create("basic", scripts, "CON.py").Succeeded,
                "template names reject traversal and Windows reserved names");
            var created = templates.Create("cli", scripts, "tool");
            var duplicate = templates.Create("cli", scripts, "tool.py");
            Verify(created.Succeeded && created.FilePath?.EndsWith("tool.py", StringComparison.OrdinalIgnoreCase) == true &&
                   duplicate.ErrorKey == "Template_Error_AlreadyExists",
                "template creation uses .py and CreateNew semantics");

            var settings = new FakeSettings { CurrentValue = new AppSettings { TerminalFontSize = 99 } };
            var appearance = new TerminalAppearanceService(settings);
            var raised = 0;
            appearance.FontSizeChanged += value => raised = value;
            appearance.SetFontSize(9);
            Verify(appearance.FontSize == 10 && raised == 10, "terminal font size is clamped to 10..24");

            var manifestPath = Path.Combine(sourceRoot, "Editor", "wwwroot", "vendor-manifest.json");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            var bundle = Path.Combine(sourceRoot, "Editor", "wwwroot", manifest.RootElement.GetProperty("bundle").GetString()!);
            var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(bundle)));
            var lockFile = Path.Combine(Directory.GetParent(sourceRoot)!.FullName, "tools", "editor-vendor", "package-lock.json");
            var lockHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(lockFile)));
            Verify(actualHash == manifest.RootElement.GetProperty("bundleSha256").GetString() &&
                   lockHash == manifest.RootElement.GetProperty("lockFileSha256").GetString() &&
                   !File.ReadAllText(Path.Combine(sourceRoot, "Editor", "wwwroot", "index.html"))
                       .Contains("http://", StringComparison.OrdinalIgnoreCase),
                "offline CodeMirror bundle matches its SHA-256 manifest and has no CDN dependency");

            var mainXaml = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml"));
            var editorHost = File.ReadAllText(Path.Combine(sourceRoot, "ViewModels", "CodeEditorViewModel.cs"));
            Verify(mainXaml.Contains("CodeWorkspace", StringComparison.Ordinal) &&
                   mainXaml.Contains("TerminalModeButton", StringComparison.Ordinal) &&
                   editorHost.Contains("PrepareToSwitchAsync", StringComparison.Ordinal) &&
                   editorHost.Contains("saveRequest", StringComparison.Ordinal),
                "terminal/code switch, unsaved guard, and Ctrl+S bridge are wired");

            var en = System.Xml.Linq.XDocument.Load(Path.Combine(sourceRoot, "Resources", "Strings.resw"));
            var zh = System.Xml.Linq.XDocument.Load(Path.Combine(sourceRoot, "Resources", "zh-CN", "Strings.resw"));
            var enKeys = en.Root!.Elements("data").Select(e => (string?)e.Attribute("name")).ToHashSet();
            var zhKeys = zh.Root!.Elements("data").Select(e => (string?)e.Attribute("name")).ToHashSet();
            Verify(enKeys.SetEquals(zhKeys) && enKeys.Contains("Settings_TerminalFontSize") &&
                   enKeys.Contains("Metrics_Summary"),
                "English and Chinese resources cover the P2 UI");

            using var connection = factory.CreateOpenConnection();
            using var version = connection.CreateCommand();
            version.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion";
            Verify(Convert.ToInt32(version.ExecuteScalar()) == 5, "database migrations reach schema version 5");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
        return failures;
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings CurrentValue { get; set; } = new();
        public AppSettings Current => CurrentValue;
        public void Update(Action<AppSettings> update) => update(CurrentValue);
        public void Flush() { }
        public void FlushOrThrow() { }
    }
}
