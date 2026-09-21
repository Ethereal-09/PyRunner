using System.Text.Json;
using PyRunner.Models;

namespace PyRunner.Services;

public sealed class DependencyInspectionService : IDependencyInspectionService
{
    private const long MaximumReportBytes = 4 * 1024 * 1024;
    private readonly IRequirementsPolicy _policy;
    private readonly IPythonProcessRunner _processes;
    private readonly TimeProvider _time;

    public DependencyInspectionService(IRequirementsPolicy policy, IPythonProcessRunner processes, TimeProvider time)
    {
        _policy = policy;
        _processes = processes;
        _time = time;
    }

    public async Task<DependencyCheckResult> InspectAsync(
        string scriptPath,
        string interpreterPath,
        bool advancedSourcesConfirmed,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        var started = _time.GetUtcNow();
        try
        {
            if (!File.Exists(scriptPath)) return Failure(started, "Dependency_Error_ScriptMissing");
            if (!File.Exists(interpreterPath)) return Failure(started, "Dependency_Error_InterpreterMissing");

            var policy = await _policy.AnalyzeAsync(scriptPath, interpreterPath, cancellationToken);
            if (!policy.Found)
                return new DependencyCheckResult(DependencyCheckStatus.NoRequirements, started, _time.GetUtcNow(),
                    null, null, [], [], string.Empty);
            if (policy.Blocked)
                return new DependencyCheckResult(DependencyCheckStatus.Failed, started, _time.GetUtcNow(),
                    null, policy.Snapshot, [], policy.Risks, "Dependency_Error_PolicyBlocked", true);
            if (policy.RequiresConfirmation && !advancedSourcesConfirmed)
                return new DependencyCheckResult(DependencyCheckStatus.ConfirmationRequired, started, _time.GetUtcNow(),
                    null, policy.Snapshot, [], policy.Risks, string.Empty);

            var directory = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;
            var pipVersion = await _processes.RunAsync(new PythonProcessRequest(
                interpreterPath, ["-m", "pip", "--version"], directory, TimeSpan.FromSeconds(10)),
                output, cancellationToken);
            if (!pipVersion.Succeeded)
                return ProcessFailure(started, policy, pipVersion, "Dependency_Error_PipUnavailable");

            var installed = await ReadInstalledAsync(interpreterPath, directory, output, cancellationToken);
            if (installed.Result is not null)
                return ProcessFailure(started, policy, installed.Result, "Dependency_Error_PipList");

            var reportRoot = Path.Combine(Path.GetTempPath(), "PyRunner", "DependencyReports");
            Directory.CreateDirectory(reportRoot);
            if ((File.GetAttributes(reportRoot) & FileAttributes.ReparsePoint) != 0)
                return Failure(started, "Dependency_Error_TemporaryDirectory");
            var report = Path.Combine(reportRoot, $"{Guid.NewGuid():N}.json");
            try
            {
                var result = await _processes.RunAsync(new PythonProcessRequest(
                    interpreterPath,
                    ["-m", "pip", "install", "--dry-run", "--report", report,
                     "--disable-pip-version-check", "--no-input", "-r", policy.Snapshot!.RequirementsPath],
                    directory,
                    TimeSpan.FromMinutes(2)), output, cancellationToken);
                if (!result.Succeeded)
                    return ProcessFailure(started, policy, result, "Dependency_Error_CheckFailed");
                if (!File.Exists(report) || new FileInfo(report).Length is <= 0 or > MaximumReportBytes)
                    return Failure(started, "Dependency_Error_ReportInvalid", policy);

                var items = await ParseReportAsync(report, policy, installed.Packages, cancellationToken);
                if (!await _policy.IsCurrentAsync(policy.Snapshot, cancellationToken))
                    return Failure(started, "Dependency_Error_ContextChanged", policy);
                var status = items.Count == 0 ? DependencyCheckStatus.Satisfied : DependencyCheckStatus.ChangesRequired;
                return new DependencyCheckResult(status, started, _time.GetUtcNow(), result.ExitCode,
                    policy.Snapshot, items, policy.Risks, result.Output);
            }
            finally
            {
                try { if (File.Exists(report)) File.Delete(report); }
                catch { }
            }
        }
        catch (OperationCanceledException)
        {
            return new DependencyCheckResult(DependencyCheckStatus.Cancelled, started, _time.GetUtcNow(),
                null, null, [], [], string.Empty);
        }
        catch (Exception ex)
        {
            return new DependencyCheckResult(DependencyCheckStatus.Failed, started, _time.GetUtcNow(),
                null, null, [], [], DependencyTextSanitizer.Sanitize(ex.Message, 2048), true);
        }
    }

    private async Task<(Dictionary<string, string> Packages, PythonProcessResult? Result)> ReadInstalledAsync(
        string interpreterPath,
        string directory,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
        var result = await _processes.RunAsync(new PythonProcessRequest(
            interpreterPath, ["-m", "pip", "list", "--format=json", "--disable-pip-version-check"],
            directory, TimeSpan.FromSeconds(30)), output, cancellationToken);
        if (!result.Succeeded) return ([], result);
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in json.RootElement.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString();
                var version = item.GetProperty("version").GetString();
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(version))
                    packages[RequirementsPolicy.NormalizePackageName(name)] = version;
            }
            return (packages, null);
        }
        catch (JsonException)
        {
            return ([], new PythonProcessResult(result.ExitCode, false, false, string.Empty,
                "Dependency_Error_PipList"));
        }
    }

    private static async Task<IReadOnlyList<DependencyItem>> ParseReportAsync(
        string reportPath,
        RequirementsPolicyResult policy,
        IReadOnlyDictionary<string, string> installed,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("install", out var install) || install.ValueKind != JsonValueKind.Array)
            throw new JsonException("pip report has no install array.");
        var items = new List<DependencyItem>();
        foreach (var entry in install.EnumerateArray())
        {
            if (!entry.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
                throw new JsonException("pip report contains an invalid install entry.");
            var name = metadata.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            var version = metadata.TryGetProperty("version", out var versionValue) ? versionValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
                throw new JsonException("pip report contains an install entry without package identity.");
            var normalized = RequirementsPolicy.NormalizePackageName(name);
            installed.TryGetValue(normalized, out var installedVersion);
            policy.RequestedByPackage.TryGetValue(normalized, out var requested);
            string? source = null;
            if (entry.TryGetProperty("download_info", out var download) &&
                download.TryGetProperty("url", out var url) && url.GetString() is { } rawUrl)
                source = DependencyTextSanitizer.SanitizeSource(rawUrl);
            items.Add(new DependencyItem(
                name,
                requested ?? name,
                installedVersion,
                version,
                installedVersion is null ? DependencyItemStatus.Missing : DependencyItemStatus.VersionMismatch,
                source));
        }
        return items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private DependencyCheckResult ProcessFailure(
        DateTimeOffset started,
        RequirementsPolicyResult policy,
        PythonProcessResult result,
        string fallbackKey)
    {
        var status = result.Cancelled ? DependencyCheckStatus.Cancelled : DependencyCheckStatus.Failed;
        var diagnostic = result.TimedOut ? "Dependency_Error_Timeout" :
            string.IsNullOrWhiteSpace(result.Output) ? fallbackKey : result.Output;
        return new DependencyCheckResult(status, started, _time.GetUtcNow(), result.ExitCode,
            policy.Snapshot, [], policy.Risks, diagnostic, status == DependencyCheckStatus.Failed);
    }

    private DependencyCheckResult Failure(
        DateTimeOffset started,
        string diagnostic,
        RequirementsPolicyResult? policy = null) =>
        new(DependencyCheckStatus.Failed, started, _time.GetUtcNow(), null,
            policy?.Snapshot, [], policy?.Risks ?? [], diagnostic, true);
}
