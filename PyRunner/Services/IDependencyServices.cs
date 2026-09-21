using PyRunner.Models;

namespace PyRunner.Services;

public interface IRequirementsPolicy
{
    Task<RequirementsPolicyResult> AnalyzeAsync(
        string scriptPath,
        string interpreterPath,
        CancellationToken cancellationToken = default);

    Task<bool> IsCurrentAsync(
        RequirementsSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

public interface IPythonProcessRunner
{
    Task<PythonProcessResult> RunAsync(
        PythonProcessRequest request,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default);
}

public interface IDependencyInspectionService
{
    Task<DependencyCheckResult> InspectAsync(
        string scriptPath,
        string interpreterPath,
        bool advancedSourcesConfirmed,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default);
}

public interface IDependencyInstallService
{
    Task<DependencyInstallResult> InstallAsync(
        DependencyCheckResult validatedCheck,
        IProgress<string>? output = null,
        CancellationToken cancellationToken = default);
}

public interface IDependencyInteractionService
{
    Task<bool> ConfirmInspectionAsync(
        IReadOnlyList<DependencyRisk> risks,
        CancellationToken cancellationToken = default);

    Task<bool> ConfirmInstallAsync(
        string interpreterName,
        string interpreterPath,
        string requirementsPath,
        int changeCount,
        IReadOnlyList<DependencyRisk> risks,
        CancellationToken cancellationToken = default);
}

public interface IDependencyInstallGuard
{
    bool IsInterpreterRunning(string interpreterPath);
    IDisposable? TryAcquire(string interpreterPath);
}
