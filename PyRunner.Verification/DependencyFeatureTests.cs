using System.Text.Json;
using PyRunner.Models;
using PyRunner.Services;
using PyRunner.ViewModels;

internal static class DependencyFeatureTests
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Verify(bool condition, string name)
        {
            if (condition) Console.WriteLine($"PASS: {name}");
            else { failures++; Console.Error.WriteLine($"FAIL: {name}"); }
        }

        var root = Path.Combine(Path.GetTempPath(), "PyRunner.DependencyVerification", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scriptDirectory = Path.Combine(root, "scripts");
            Directory.CreateDirectory(scriptDirectory);
            var script = Path.Combine(scriptDirectory, "sample.py");
            var interpreter = Path.Combine(root, "python.exe");
            await File.WriteAllTextAsync(script, "print('safe')");
            await File.WriteAllBytesAsync(interpreter, [0x4d, 0x5a]);
            var requirements = Path.Combine(scriptDirectory, "requirements.txt");
            var policy = new RequirementsPolicy();

            var missing = await policy.AnalyzeAsync(script, interpreter);
            Verify(!missing.Found && missing.Snapshot is null,
                "dependency discovery treats a missing same-directory requirements.txt as non-error");

            var nestedDirectory = Path.Combine(scriptDirectory, "constraints");
            Directory.CreateDirectory(nestedDirectory);
            var nested = Path.Combine(nestedDirectory, "base.txt");
            await File.WriteAllTextAsync(nested, "requests>=2.32\n");
            await File.WriteAllTextAsync(requirements, "-c constraints/base.txt\nflask==3.0.0\n");
            var normal = await policy.AnalyzeAsync(script, interpreter);
            Verify(normal is { Found: true, Blocked: false, Snapshot.Files.Count: 2 } &&
                   normal.RequestedByPackage.ContainsKey("requests") &&
                   normal.RequestedByPackage.ContainsKey("flask"),
                "requirements policy fingerprints same-tree recursive constraints and package requests");
            await File.AppendAllTextAsync(nested, "urllib3>=2\n");
            Verify(normal.Snapshot is not null && !await policy.IsCurrentAsync(normal.Snapshot),
                "nested requirements content change invalidates the complete snapshot");

            await File.WriteAllTextAsync(requirements, "--trusted-host pypi.example\nrequests\n");
            var trustedHost = await policy.AnalyzeAsync(script, interpreter);
            Verify(trustedHost.Blocked && trustedHost.Risks.Any(r => r.Kind == "trusted-host"),
                "trusted-host is blocked before pip execution");

            await File.WriteAllTextAsync(requirements, "-r https://user:secret@example.test/req.txt?token=abc\n");
            var remoteInclude = await policy.AnalyzeAsync(script, interpreter);
            Verify(remoteInclude.Blocked && remoteInclude.Risks.All(r =>
                    !r.DisplaySource.Contains("secret", StringComparison.Ordinal) &&
                    !r.DisplaySource.Contains("token=abc", StringComparison.Ordinal)),
                "remote includes are blocked and displayed without URL credentials or tokens");

            var outside = Path.Combine(root, "outside.whl");
            await File.WriteAllTextAsync(outside, "not a wheel");
            await File.WriteAllTextAsync(requirements, "..\\outside.whl\n");
            var traversal = await policy.AnalyzeAsync(script, interpreter);
            Verify(traversal.Blocked && traversal.Risks.Any(r => r.Kind == "local-source"),
                "local dependency traversal outside the script directory is blocked");

            await File.WriteAllTextAsync(requirements,
                "--extra-index-url=https://user:password@packages.example/simple?key=123\nrequests>=2.32\n");
            var advanced = await policy.AnalyzeAsync(script, interpreter);
            Verify(!advanced.Blocked && advanced.RequiresConfirmation && advanced.Risks.All(r =>
                    !r.DisplaySource.Contains("password", StringComparison.Ordinal) &&
                    !r.DisplaySource.Contains("key=123", StringComparison.Ordinal)),
                "HTTPS alternate indexes require confirmation and are redacted");

            await File.WriteAllTextAsync(requirements, "requests>=2.32\nflask==3.0.0\n");
            var process = new FakeProcessRunner();
            process.Handler = request =>
            {
                if (request.Arguments.Contains("--version"))
                    return Success("pip 26.0");
                if (request.Arguments.Contains("--format=json"))
                    return Success("[{\"name\":\"requests\",\"version\":\"2.0.0\"}]");
                if (request.Arguments.Contains("--dry-run"))
                {
                    var report = request.Arguments[request.Arguments.IndexOf("--report") + 1];
                    File.WriteAllText(report, JsonSerializer.Serialize(new
                    {
                        install = new object[]
                        {
                            new { metadata = new { name = "requests", version = "2.32.0" }, download_info = new { url = "https://pypi.org/a" } },
                            new { metadata = new { name = "flask", version = "3.0.0" }, download_info = new { url = "https://pypi.org/b" } },
                        }
                    }));
                    process.ReportPaths.Add(report);
                    return Success("dry run complete");
                }
                return Failure("unexpected command");
            };
            var inspection = new DependencyInspectionService(policy, process, TimeProvider.System);
            var checkedResult = await inspection.InspectAsync(script, interpreter, false);
            var dryRunRequest = process.Requests.Single(r => r.Arguments.Contains("--dry-run"));
            Verify(checkedResult.Status == DependencyCheckStatus.ChangesRequired &&
                   checkedResult.Items.Count == 2 &&
                   checkedResult.Items.Any(i => i.Name == "requests" && i.Status == DependencyItemStatus.VersionMismatch) &&
                   checkedResult.Items.Any(i => i.Name == "flask" && i.Status == DependencyItemStatus.Missing) &&
                   dryRunRequest.InterpreterPath == interpreter &&
                   !dryRunRequest.Arguments.Contains("--ignore-installed") &&
                   process.ReportPaths.All(path => !File.Exists(path)),
                "pip dry-run report distinguishes missing and version changes and cleans its unique report");

            var guard = new FakeGuard();
            var installRunner = new FakeProcessRunner
            {
                Handler = request => request.Arguments.Contains("install") || request.Arguments.Contains("check")
                    ? Success(string.Empty)
                    : Failure("unexpected command")
            };
            var refreshed = new FakeInspectionService
            {
                Result = checkedResult with { Status = DependencyCheckStatus.Satisfied, Items = [] }
            };
            var installerService = new DependencyInstallService(policy, installRunner, refreshed, guard);
            var installResult = await installerService.InstallAsync(checkedResult);
            Verify(installResult.Succeeded && refreshed.CallCount == 1 &&
                   installRunner.Requests.Any(r => r.Arguments.SequenceEqual(new[]
                   {
                       "-m", "pip", "install", "--disable-pip-version-check", "--no-input", "-r", requirements
                   })) &&
                   installRunner.Requests.Any(r => r.Arguments.SequenceEqual(new[]
                   {
                       "-m", "pip", "check", "--disable-pip-version-check"
                   })),
                "confirmed install service uses exact interpreter ArgumentList then rechecks dependencies and pip compatibility");

            guard.Busy = true;
            var beforeBusyCount = installRunner.Requests.Count;
            var busyResult = await installerService.InstallAsync(checkedResult);
            Verify(busyResult.InterpreterBusy && installRunner.Requests.Count == beforeBusyCount,
                "running scripts using the interpreter prevent pip install from starting");
            guard.Busy = false;

            await File.AppendAllTextAsync(requirements, "urllib3>=2\n");
            var beforeChangedContextCount = installRunner.Requests.Count;
            var changedContext = await installerService.InstallAsync(checkedResult);
            Verify(changedContext.ContextChanged &&
                   installRunner.Requests.Count == beforeChangedContextCount,
                "requirements changed after checking never reach pip install");
            await File.WriteAllTextAsync(requirements, "requests>=2.32\nflask==3.0.0\n");

            var cancellationRunner = new CancellationProcessRunner();
            var cancellationInspection = new DependencyInspectionService(policy, cancellationRunner, TimeProvider.System);
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelledTask = cancellationInspection.InspectAsync(
                    script, interpreter, false, cancellationToken: cancellation.Token);
                await cancellationRunner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                cancellation.Cancel();
                var cancelled = await cancelledTask;
                Verify(cancelled.Status == DependencyCheckStatus.Cancelled && cancellationRunner.CallCount == 1,
                    "dependency checking cancellation stops before later pip commands");
            }

            var fakeInspection = new FakeInspectionService { Result = checkedResult };
            var fakeInstaller = new FakeInstallService();
            var interaction = new FakeInteraction { InstallAccepted = false };
            using (var viewModel = new ScriptDependencyViewModel(
                       fakeInspection, fakeInstaller, interaction, guard,
                       new KeyLocalization(), new FakeClipboard(), InlineUiDispatcher.Instance))
            {
                viewModel.SetContext(
                    new Script { Id = 1, FilePath = script, InterpreterId = 1 },
                    new Interpreter { Id = 1, Name = "Test Python", ExecutablePath = interpreter });
                await viewModel.CheckCommand.ExecuteAsync(null);
                await viewModel.InstallCommand.ExecuteAsync(null);
                Verify(fakeInstaller.CallCount == 0 && interaction.InstallConfirmCount == 1,
                    "user rejection never invokes the dependency install service");
            }

            var blockingInspection = new BlockingInspectionService(checkedResult);
            using (var viewModel = new ScriptDependencyViewModel(
                       blockingInspection, new FakeInstallService(), new FakeInteraction(), guard,
                       new KeyLocalization(), new FakeClipboard(), InlineUiDispatcher.Instance))
            {
                viewModel.SetContext(
                    new Script { Id = 1, FilePath = script, InterpreterId = 1 },
                    new Interpreter { Id = 1, Name = "Test Python", ExecutablePath = interpreter });
                var first = viewModel.CheckCommand.ExecuteAsync(null);
                await blockingInspection.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var duplicate = viewModel.CheckCommand.ExecuteAsync(null);
                Verify(blockingInspection.CallCount == 1 && !viewModel.CanInstall,
                    "duplicate dependency checks share command exclusion and stale results cannot authorize install");
                blockingInspection.Release.TrySetResult();
                await Task.WhenAll(first, duplicate);
            }

            var cancellingInstaller = new CancellationInstallService();
            using (var viewModel = new ScriptDependencyViewModel(
                       new FakeInspectionService { Result = checkedResult }, cancellingInstaller,
                       new FakeInteraction { InstallAccepted = true }, guard,
                       new KeyLocalization(), new FakeClipboard(), InlineUiDispatcher.Instance))
            {
                viewModel.SetContext(
                    new Script { Id = 1, FilePath = script, InterpreterId = 1 },
                    new Interpreter { Id = 1, Name = "Test Python", ExecutablePath = interpreter });
                await viewModel.CheckCommand.ExecuteAsync(null);
                var install = viewModel.InstallCommand.ExecuteAsync(null);
                await cancellingInstaller.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var duplicateInstall = viewModel.InstallCommand.ExecuteAsync(null);
                viewModel.CancelCommand.Execute(null);
                await Task.WhenAll(install, duplicateInstall);
                Verify(cancellingInstaller.CallCount == 1 && !viewModel.CanInstall,
                    "cancelled and duplicate dependency installation cannot reuse the pre-install plan");
            }

            Verify(
                !DependencyTextSanitizer.Sanitize(
                    "https://user:pass@example.test/a?token=abc Authorization=secret")
                    .Contains("pass", StringComparison.OrdinalIgnoreCase) &&
                !DependencyTextSanitizer.Sanitize(
                    "https://user:pass@example.test/a?token=abc Authorization=secret")
                    .Contains("abc", StringComparison.OrdinalIgnoreCase) &&
                !DependencyTextSanitizer.Sanitize("Authorization: Bearer top-secret-value")
                    .Contains("top-secret-value", StringComparison.OrdinalIgnoreCase) &&
                DependencyTextSanitizer.Sanitize(new string('x', 100_000)).Length ==
                    DependencyTextSanitizer.MaximumDiagnosticLength,
                "dependency diagnostics redact credentials and cap oversized output");

            var appSource = await File.ReadAllTextAsync(Path.Combine(FindSolutionRoot(), "PyRunner", "App.xaml.cs"));
            var viewSource = await File.ReadAllTextAsync(Path.Combine(FindSolutionRoot(), "PyRunner", "MainWindow.xaml"));
            var en = await File.ReadAllTextAsync(Path.Combine(FindSolutionRoot(), "PyRunner", "Resources", "Strings.resw"));
            var zh = await File.ReadAllTextAsync(Path.Combine(FindSolutionRoot(), "PyRunner", "Resources", "zh-CN", "Strings.resw"));
            Verify(appSource.Contains("IDependencyInspectionService", StringComparison.Ordinal) &&
                   viewSource.Contains("DependencyExpander", StringComparison.Ordinal) &&
                   viewSource.Contains("DependencyInstallButton", StringComparison.Ordinal) &&
                   en.Contains("Dependency_InstallConfirmTitle", StringComparison.Ordinal) &&
                   zh.Contains("Dependency_InstallConfirmTitle", StringComparison.Ordinal),
                "dependency services, compact script detail UI, confirmation and bilingual resources are wired");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        return failures;
    }

    private static PythonProcessResult Success(string stdout) => new(0, false, false, stdout, string.Empty);
    private static PythonProcessResult Failure(string stderr) => new(1, false, false, string.Empty, stderr);

    private sealed class FakeProcessRunner : IPythonProcessRunner
    {
        public Func<PythonProcessRequest, PythonProcessResult> Handler { get; set; } = _ => Success(string.Empty);
        public List<PythonProcessRequest> Requests { get; } = [];
        public List<string> ReportPaths { get; } = [];
        public Task<PythonProcessResult> RunAsync(PythonProcessRequest request, IProgress<string>? output = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Handler(request));
        }
    }

    private sealed class CancellationProcessRunner : IPythonProcessRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public async Task<PythonProcessResult> RunAsync(PythonProcessRequest request,
            IProgress<string>? output = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Success(string.Empty);
            }
            catch (OperationCanceledException)
            {
                return new PythonProcessResult(null, true, false, string.Empty, string.Empty);
            }
        }
    }

    private sealed class FakeInspectionService : IDependencyInspectionService
    {
        public required DependencyCheckResult Result { get; init; }
        public int CallCount { get; private set; }
        public Task<DependencyCheckResult> InspectAsync(string scriptPath, string interpreterPath,
            bool advancedSourcesConfirmed, IProgress<string>? output = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class BlockingInspectionService : IDependencyInspectionService
    {
        private readonly DependencyCheckResult _result;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public BlockingInspectionService(DependencyCheckResult result) => _result = result;

        public async Task<DependencyCheckResult> InspectAsync(string scriptPath, string interpreterPath,
            bool advancedSourcesConfirmed, IProgress<string>? output = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return _result;
        }
    }

    private sealed class FakeInstallService : IDependencyInstallService
    {
        public int CallCount { get; private set; }
        public Task<DependencyInstallResult> InstallAsync(DependencyCheckResult validatedCheck,
            IProgress<string>? output = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new DependencyInstallResult(true, false, false, false, false, 0,
                string.Empty, string.Empty, validatedCheck with { Status = DependencyCheckStatus.Satisfied, Items = [] }));
        }
    }

    private sealed class CancellationInstallService : IDependencyInstallService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public async Task<DependencyInstallResult> InstallAsync(DependencyCheckResult validatedCheck,
            IProgress<string>? output = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { }
            return new DependencyInstallResult(false, true, false, false, false, null,
                string.Empty, string.Empty, null);
        }
    }

    private sealed class FakeInteraction : IDependencyInteractionService
    {
        public bool InstallAccepted { get; set; }
        public int InstallConfirmCount { get; private set; }
        public Task<bool> ConfirmInspectionAsync(IReadOnlyList<DependencyRisk> risks,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> ConfirmInstallAsync(string interpreterName, string interpreterPath,
            string requirementsPath, int changeCount, IReadOnlyList<DependencyRisk> risks,
            CancellationToken cancellationToken = default)
        {
            InstallConfirmCount++;
            return Task.FromResult(InstallAccepted);
        }
    }

    private sealed class FakeGuard : IDependencyInstallGuard
    {
        public bool Busy { get; set; }
        public bool IsInterpreterRunning(string interpreterPath) => Busy;
        public IDisposable? TryAcquire(string interpreterPath) => Busy ? null : new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }

    private sealed class KeyLocalization : ILocalizationService
    {
        public string CurrentLanguage => "en-US";
        public string this[string key] => key;
        public event Action? LanguageChanged { add { } remove { } }
        public void SetLanguage(string languageCode) { }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public void CopyText(string text) { }
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PyRunner.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate PyRunner.sln");
    }
}

internal static class DependencyTestListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
            if (string.Equals(values[index], value, StringComparison.Ordinal)) return index;
        return -1;
    }
}
