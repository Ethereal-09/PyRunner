namespace PyRunner.Services;

public sealed class DependencyInstallGuard : IDependencyInstallGuard
{
    private readonly RunCoordinator _runs;

    public DependencyInstallGuard(RunCoordinator runs) => _runs = runs;

    public bool IsInterpreterRunning(string interpreterPath) =>
        _runs.IsInterpreterRunning(interpreterPath);

    public IDisposable? TryAcquire(string interpreterPath) =>
        _runs.TryAcquireDependencyInstallLease(interpreterPath);
}
