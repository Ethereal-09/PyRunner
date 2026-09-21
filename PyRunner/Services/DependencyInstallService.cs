using PyRunner.Models;

namespace PyRunner.Services;

public sealed class DependencyInstallService : IDependencyInstallService
{
    private readonly IRequirementsPolicy _policy;
    private readonly IPythonProcessRunner _processes;
    private readonly IDependencyInspectionService _inspection;
    private readonly IDependencyInstallGuard _guard;

    public DependencyInstallService(
        IRequirementsPolicy policy,
        IPythonProcessRunner processes,
        IDependencyInspectionService inspection,
        IDependencyInstallGuard guard)
    {
        _policy = policy;
        _processes = processes;
        _inspection = inspection;
        _guard = guard;
    }

    public async Task<DependencyInstallResult> InstallAsync(
        DependencyCheckResult validatedCheck,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default)
    {
        if (validatedCheck is not { Status: DependencyCheckStatus.ChangesRequired, Snapshot: { } snapshot } ||
            validatedCheck.InstallationBlocked || !validatedCheck.HasInstallPlan)
            return Invalid("Dependency_Error_ResultInvalid");

        if (!await _policy.IsCurrentAsync(snapshot, cancellationToken))
            return new DependencyInstallResult(false, false, false, true, false, null,
                "Dependency_Error_ContextChanged", string.Empty, null);

        using var lease = _guard.TryAcquire(snapshot.InterpreterPath);
        if (lease is null)
            return new DependencyInstallResult(false, false, false, false, true, null,
                "Dependency_Error_InterpreterBusy", string.Empty, null);

        if (!await _policy.IsCurrentAsync(snapshot, cancellationToken))
            return new DependencyInstallResult(false, false, false, true, false, null,
                "Dependency_Error_ContextChanged", string.Empty, null);

        var directory = Path.GetDirectoryName(snapshot.ScriptPath)!;
        var install = await _processes.RunAsync(new PythonProcessRequest(
            snapshot.InterpreterPath,
            ["-m", "pip", "install", "--disable-pip-version-check", "--no-input", "-r", snapshot.RequirementsPath],
            directory,
            TimeSpan.FromMinutes(10)), output, cancellationToken);

        DependencyCheckResult? refreshed = null;
        if (!install.Cancelled && !install.TimedOut)
        {
            refreshed = await _inspection.InspectAsync(
                snapshot.ScriptPath, snapshot.InterpreterPath, true, output, cancellationToken);
        }

        var compatibility = string.Empty;
        var cancelled = install.Cancelled || refreshed?.Status == DependencyCheckStatus.Cancelled;
        if (install.Succeeded && !cancelled)
        {
            var check = await _processes.RunAsync(new PythonProcessRequest(
                snapshot.InterpreterPath,
                ["-m", "pip", "check", "--disable-pip-version-check"],
                directory,
                TimeSpan.FromMinutes(2)), output, cancellationToken);
            compatibility = check.Succeeded ? string.Empty :
                string.IsNullOrWhiteSpace(check.Output) ? "Dependency_Error_PipCheck" : check.Output;
        }

        var succeeded = install.Succeeded && refreshed?.Status == DependencyCheckStatus.Satisfied;
        var diagnostic = install.Output;
        if (install.Succeeded && refreshed is not null && refreshed.Status != DependencyCheckStatus.Satisfied)
            diagnostic = string.IsNullOrWhiteSpace(refreshed.Diagnostic)
                ? "Dependency_Error_RecheckFailed"
                : refreshed.Diagnostic;

        return new DependencyInstallResult(
            succeeded,
            cancelled,
            install.TimedOut,
            false,
            false,
            install.ExitCode,
            diagnostic,
            compatibility,
            refreshed);
    }

    private static DependencyInstallResult Invalid(string diagnostic) =>
        new(false, false, false, false, false, null, diagnostic, string.Empty, null);
}
