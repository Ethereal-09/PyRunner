namespace PyRunner.Models;

public enum DependencyCheckStatus
{
    NotChecked,
    NoRequirements,
    Checking,
    ConfirmationRequired,
    Satisfied,
    ChangesRequired,
    Failed,
    Cancelled,
    Installing,
}

public enum DependencyItemStatus
{
    Missing,
    VersionMismatch,
}

public enum DependencyRiskSeverity
{
    Notice,
    Warning,
    Blocked,
}

public sealed record RequirementsFileFingerprint(
    string Path,
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256);

public sealed record RequirementsSnapshot(
    string ScriptPath,
    string InterpreterPath,
    string RequirementsPath,
    string Fingerprint,
    IReadOnlyList<RequirementsFileFingerprint> Files);

public sealed record DependencyRisk(
    DependencyRiskSeverity Severity,
    string Kind,
    string DisplaySource,
    string MessageKey);

public sealed record DependencyItem(
    string Name,
    string Requested,
    string? InstalledVersion,
    string PlannedVersion,
    DependencyItemStatus Status,
    string? DisplaySource);

public sealed record DependencyCheckResult(
    DependencyCheckStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int? ExitCode,
    RequirementsSnapshot? Snapshot,
    IReadOnlyList<DependencyItem> Items,
    IReadOnlyList<DependencyRisk> Risks,
    string Diagnostic,
    bool InstallationBlocked = false)
{
    public bool HasInstallPlan => Items.Count != 0;
}

public sealed record DependencyInstallResult(
    bool Succeeded,
    bool Cancelled,
    bool TimedOut,
    bool ContextChanged,
    bool InterpreterBusy,
    int? ExitCode,
    string Diagnostic,
    string CompatibilityDiagnostic,
    DependencyCheckResult? RefreshedCheck);

public sealed record RequirementsPolicyResult(
    bool Found,
    bool Blocked,
    bool RequiresConfirmation,
    RequirementsSnapshot? Snapshot,
    IReadOnlyList<DependencyRisk> Risks,
    IReadOnlyDictionary<string, string> RequestedByPackage);

public sealed record PythonProcessRequest(
    string InterpreterPath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout);

public sealed record PythonProcessResult(
    int? ExitCode,
    bool Cancelled,
    bool TimedOut,
    string StandardOutput,
    string StandardError)
{
    public bool Succeeded => ExitCode == 0 && !Cancelled && !TimedOut;
    public string Output => string.Join(Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
}
