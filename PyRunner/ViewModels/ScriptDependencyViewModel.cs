using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyRunner.Models;
using PyRunner.Services;

namespace PyRunner.ViewModels;

public sealed partial class ScriptDependencyViewModel : ObservableObject, IDisposable
{
    private readonly IDependencyInspectionService _inspection;
    private readonly IDependencyInstallService _installer;
    private readonly IDependencyInteractionService _interaction;
    private readonly IDependencyInstallGuard _guard;
    private readonly ILocalizationService _localization;
    private readonly IClipboardService _clipboard;
    private readonly IUiDispatcher _dispatcher;
    private CancellationTokenSource? _operationCancellation;
    private FileSystemWatcher? _requirementsWatcher;
    private DependencyCheckResult? _result;
    private long _contextGeneration;
    private bool _disposed;

    [ObservableProperty] private bool hasContext;
    [ObservableProperty] private bool isExpanded;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private string summaryText = string.Empty;
    [ObservableProperty] private string requirementsPath = string.Empty;
    [ObservableProperty] private string interpreterPath = string.Empty;
    [ObservableProperty] private string interpreterName = string.Empty;
    [ObservableProperty] private string diagnosticText = string.Empty;
    [ObservableProperty] private string compatibilityText = string.Empty;
    [ObservableProperty] private bool hasRequirements;
    [ObservableProperty] private bool hasRisks;
    [ObservableProperty] private bool hasDiagnostic;

    public ObservableCollection<DependencyItemRowViewModel> Items { get; } = [];
    public ObservableCollection<string> RiskMessages { get; } = [];

    public IAsyncRelayCommand CheckCommand { get; }
    public IAsyncRelayCommand InstallCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand CopyRequirementsPathCommand { get; }
    public IRelayCommand CopyInterpreterPathCommand { get; }

    public bool CanCheck => HasContext && HasRequirements && !IsBusy;
    public bool CanInstall => _result is
        { Status: DependencyCheckStatus.ChangesRequired, InstallationBlocked: false, HasInstallPlan: true, Snapshot: { } snapshot } &&
        !IsBusy && !_guard.IsInterpreterRunning(snapshot.InterpreterPath);
    public bool CanCancel => IsBusy;
    public bool HasItems => Items.Count != 0;
    public string SectionTitle => _localization["Dependency_Title"];
    public string NetworkNotice => _localization["Dependency_NetworkNotice"];
    public string RequirementsLabel => _localization["Dependency_RequirementsLabel"];
    public string InterpreterLabel => _localization["Dependency_InterpreterLabel"];
    public string CheckText => _localization[_result is null ? "Dependency_Check" : "Dependency_Recheck"];
    public string InstallText => _localization["Dependency_Install"];
    public string CancelText => _localization["Button_Cancel"];
    public string DetailsTitle => _localization["Dependency_Details"];
    public string CompatibilityTitle => _localization["Dependency_Compatibility"];
    public string CopyText => _localization["Button_Copy"];
    public string RisksTitle => _localization["Dependency_Risks"];
    public string CommandPreview => HasContext
        ? $"python.exe -m pip install --disable-pip-version-check --no-input -r \"{RequirementsPath}\""
        : string.Empty;

    public ScriptDependencyViewModel(
        IDependencyInspectionService inspection,
        IDependencyInstallService installer,
        IDependencyInteractionService interaction,
        IDependencyInstallGuard guard,
        ILocalizationService localization,
        IClipboardService clipboard,
        IUiDispatcher dispatcher)
    {
        _inspection = inspection;
        _installer = installer;
        _interaction = interaction;
        _guard = guard;
        _localization = localization;
        _clipboard = clipboard;
        _dispatcher = dispatcher;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => CanCheck);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => CanInstall);
        CancelCommand = new RelayCommand(Cancel, () => CanCancel);
        CopyRequirementsPathCommand = new RelayCommand(
            () => _clipboard.CopyText(RequirementsPath), () => HasContext && HasRequirements);
        CopyInterpreterPathCommand = new RelayCommand(
            () => _clipboard.CopyText(InterpreterPath), () => HasContext);
        _localization.LanguageChanged += OnLanguageChanged;
        ClearContext();
    }

    public void SetContext(Script? script, Interpreter? interpreter)
    {
        if (_disposed) return;
        var scriptPath = script?.FilePath;
        var interpreterPathValue = interpreter?.ExecutablePath;
        if (string.IsNullOrWhiteSpace(scriptPath) || string.IsNullOrWhiteSpace(interpreterPathValue))
        {
            ClearContext();
            return;
        }

        string normalizedScript;
        string normalizedInterpreter;
        try
        {
            normalizedScript = Path.GetFullPath(scriptPath);
            normalizedInterpreter = Path.GetFullPath(interpreterPathValue);
        }
        catch
        {
            ClearContext();
            return;
        }
        if (HasContext && string.Equals(_scriptPath, normalizedScript, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(InterpreterPath, normalizedInterpreter, StringComparison.OrdinalIgnoreCase))
            return;

        Cancel();
        _contextGeneration++;
        _scriptPath = normalizedScript;
        InterpreterPath = normalizedInterpreter;
        InterpreterName = interpreter?.Name ?? Path.GetFileNameWithoutExtension(normalizedInterpreter);
        RequirementsPath = Path.Combine(Path.GetDirectoryName(normalizedScript)!, "requirements.txt");
        HasContext = true;
        IsExpanded = false;
        ResetResult();
        RefreshFilePresence();
        StartWatcher(Path.GetDirectoryName(normalizedScript)!);
        NotifyStateChanged();
    }

    public void RefreshRunState() => NotifyStateChanged();

    private string _scriptPath = string.Empty;

    private async Task CheckAsync()
    {
        if (!CanCheck) return;
        var generation = _contextGeneration;
        var scriptPath = _scriptPath;
        var interpreterPath = InterpreterPath;
        // Keep the previous rows visible as stale information, but never allow an
        // earlier result to authorize installation while a recheck is in flight.
        _result = null;
        var operation = BeginOperation("Dependency_Status_Checking");
        try
        {
            var progress = CreateProgress();
            var result = await _inspection.InspectAsync(
                scriptPath, interpreterPath, false, progress, operation.Token);
            if (!IsCurrent(generation, scriptPath, interpreterPath)) return;
            if (result.Status == DependencyCheckStatus.ConfirmationRequired)
            {
                ApplyResult(result);
                var confirmed = await _interaction.ConfirmInspectionAsync(result.Risks, operation.Token);
                if (!confirmed || !IsCurrent(generation, scriptPath, interpreterPath))
                {
                    StatusText = _localization["Dependency_Status_ConfirmationDeclined"];
                    return;
                }
                result = await _inspection.InspectAsync(
                    scriptPath, interpreterPath, true, progress, operation.Token);
            }
            if (IsCurrent(generation, scriptPath, interpreterPath)) ApplyResult(result);
        }
        catch (OperationCanceledException)
        {
            if (generation == _contextGeneration) StatusText = _localization["Dependency_Status_Cancelled"];
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private async Task InstallAsync()
    {
        if (!CanInstall || _result?.Snapshot is not { } snapshot) return;
        var generation = _contextGeneration;
        var validated = _result;
        var operation = BeginOperation("Dependency_Status_Confirming");
        try
        {
            var confirmed = await _interaction.ConfirmInstallAsync(
                InterpreterName,
                InterpreterPath,
                RequirementsPath,
                validated.Items.Count,
                validated.Risks,
                operation.Token);
            if (!confirmed || !IsCurrent(generation, snapshot.ScriptPath, snapshot.InterpreterPath))
            {
                StatusText = _localization["Dependency_Status_InstallDeclined"];
                return;
            }

            // pip may partially mutate the environment even when it later fails,
            // times out, or is cancelled. Retire the pre-install plan before the
            // process starts; only a refreshed inspection may authorize a retry.
            _result = null;
            StatusText = _localization["Dependency_Status_Installing"];
            NotifyStateChanged();
            var result = await _installer.InstallAsync(validated, CreateProgress(), operation.Token);
            if (!IsCurrent(generation, snapshot.ScriptPath, snapshot.InterpreterPath)) return;
            CompatibilityText = ResolveDiagnostic(result.CompatibilityDiagnostic);
            if (result.RefreshedCheck is not null)
                ApplyResult(result.RefreshedCheck);
            if (result.Succeeded)
                StatusText = string.IsNullOrWhiteSpace(CompatibilityText)
                    ? _localization["Dependency_Status_InstallSucceeded"]
                    : _localization["Dependency_Status_InstallSucceededWithConflicts"];
            else if (result.Cancelled)
                StatusText = _localization["Dependency_Status_Cancelled"];
            else if (result.ContextChanged)
                Invalidate("Dependency_Error_ContextChanged");
            else if (result.InterpreterBusy)
                StatusText = _localization["Dependency_Error_InterpreterBusy"];
            else
            {
                StatusText = _localization["Dependency_Status_InstallFailed"];
                DiagnosticText = ResolveDiagnostic(result.TimedOut ? "Dependency_Error_Timeout" : result.Diagnostic);
                HasDiagnostic = DiagnosticText.Length != 0;
            }
        }
        catch (OperationCanceledException)
        {
            if (generation == _contextGeneration) StatusText = _localization["Dependency_Status_Cancelled"];
        }
        finally
        {
            EndOperation(operation);
        }
    }

    private void ApplyResult(DependencyCheckResult result)
    {
        _result = result;
        Items.Clear();
        foreach (var item in result.Items)
            Items.Add(new DependencyItemRowViewModel(item, _localization));
        RiskMessages.Clear();
        foreach (var risk in result.Risks)
            RiskMessages.Add($"{_localization[risk.MessageKey]}: {risk.DisplaySource}");
        HasRisks = RiskMessages.Count != 0;
        HasDiagnostic = !string.IsNullOrWhiteSpace(result.Diagnostic);
        DiagnosticText = ResolveDiagnostic(result.Diagnostic);
        SummaryText = result.Status switch
        {
            DependencyCheckStatus.NoRequirements => _localization["Dependency_Status_NoFile"],
            DependencyCheckStatus.Satisfied => _localization["Dependency_Status_Satisfied"],
            DependencyCheckStatus.ChangesRequired => string.Format(_localization["Dependency_Status_Changes"], result.Items.Count),
            DependencyCheckStatus.ConfirmationRequired => _localization["Dependency_Status_ConfirmationRequired"],
            DependencyCheckStatus.Cancelled => _localization["Dependency_Status_Cancelled"],
            DependencyCheckStatus.Failed => _localization["Dependency_Status_Failed"],
            _ => _localization["Dependency_Status_NotChecked"],
        };
        StatusText = SummaryText;
        NotifyStateChanged();
    }

    private void RefreshFilePresence()
    {
        HasRequirements = HasContext && File.Exists(RequirementsPath);
        SummaryText = HasRequirements
            ? _localization["Dependency_Status_NotChecked"]
            : _localization["Dependency_Status_NoFile"];
        StatusText = SummaryText;
    }

    private void Invalidate(string key = "Dependency_Status_Expired")
    {
        Cancel();
        _contextGeneration++;
        ResetResult();
        RefreshFilePresence();
        if (HasRequirements) StatusText = SummaryText = _localization[key];
        NotifyStateChanged();
    }

    private void ResetResult()
    {
        _result = null;
        Items.Clear();
        RiskMessages.Clear();
        DiagnosticText = string.Empty;
        CompatibilityText = string.Empty;
        HasRisks = false;
        HasDiagnostic = false;
    }

    private void ClearContext()
    {
        Cancel();
        _contextGeneration++;
        StopWatcher();
        _scriptPath = string.Empty;
        InterpreterPath = string.Empty;
        InterpreterName = string.Empty;
        RequirementsPath = string.Empty;
        HasContext = false;
        HasRequirements = false;
        IsExpanded = false;
        ResetResult();
        StatusText = SummaryText = _localization["Dependency_Status_NoContext"];
        NotifyStateChanged();
    }

    private CancellationTokenSource BeginOperation(string statusKey)
    {
        _operationCancellation?.Dispose();
        var operation = new CancellationTokenSource();
        _operationCancellation = operation;
        IsBusy = true;
        DiagnosticText = string.Empty;
        CompatibilityText = string.Empty;
        HasDiagnostic = false;
        StatusText = _localization[statusKey];
        NotifyStateChanged();
        return operation;
    }

    private void EndOperation(CancellationTokenSource operation)
    {
        if (!ReferenceEquals(_operationCancellation, operation))
        {
            operation.Dispose();
            return;
        }
        IsBusy = false;
        operation.Dispose();
        _operationCancellation = null;
        NotifyStateChanged();
    }

    private void Cancel() => _operationCancellation?.Cancel();

    private IProgress<string> CreateProgress() => new Progress<string>(chunk =>
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            var combined = DiagnosticText + chunk;
            DiagnosticText = combined.Length <= DependencyTextSanitizer.MaximumDiagnosticLength
                ? combined
                : combined[^DependencyTextSanitizer.MaximumDiagnosticLength..];
            HasDiagnostic = DiagnosticText.Length != 0;
        }));

    private bool IsCurrent(long generation, string scriptPath, string interpreterPath) =>
        !_disposed && generation == _contextGeneration &&
        string.Equals(_scriptPath, scriptPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(InterpreterPath, interpreterPath, StringComparison.OrdinalIgnoreCase);

    private string ResolveDiagnostic(string diagnostic) =>
        diagnostic.StartsWith("Dependency_", StringComparison.Ordinal) ? _localization[diagnostic] : diagnostic;

    private void StartWatcher(string directory)
    {
        StopWatcher();
        if (!Directory.Exists(directory)) return;
        try
        {
            _requirementsWatcher = new FileSystemWatcher(directory, "*")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            _requirementsWatcher.Changed += OnRequirementsChanged;
            _requirementsWatcher.Created += OnRequirementsChanged;
            _requirementsWatcher.Deleted += OnRequirementsChanged;
            _requirementsWatcher.Renamed += OnRequirementsChanged;
        }
        catch
        {
            StopWatcher();
        }
    }

    private void OnRequirementsChanged(object sender, FileSystemEventArgs e)
    {
        string changedPath;
        try { changedPath = Path.GetFullPath(e.FullPath); }
        catch { return; }

        var affectsSnapshot = string.Equals(changedPath, RequirementsPath, StringComparison.OrdinalIgnoreCase) ||
                              _result?.Snapshot?.Files.Any(file =>
                                  string.Equals(file.Path, changedPath, StringComparison.OrdinalIgnoreCase)) == true;
        if (!affectsSnapshot) return;
        _dispatcher.TryEnqueue(() => { if (!_disposed) Invalidate(); });
    }

    private void StopWatcher()
    {
        if (_requirementsWatcher is null) return;
        _requirementsWatcher.EnableRaisingEvents = false;
        _requirementsWatcher.Changed -= OnRequirementsChanged;
        _requirementsWatcher.Created -= OnRequirementsChanged;
        _requirementsWatcher.Deleted -= OnRequirementsChanged;
        _requirementsWatcher.Renamed -= OnRequirementsChanged;
        _requirementsWatcher.Dispose();
        _requirementsWatcher = null;
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanCheck));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CheckText));
        OnPropertyChanged(nameof(CommandPreview));
        CheckCommand?.NotifyCanExecuteChanged();
        InstallCommand?.NotifyCanExecuteChanged();
        CancelCommand?.NotifyCanExecuteChanged();
        CopyRequirementsPathCommand?.NotifyCanExecuteChanged();
        CopyInterpreterPathCommand?.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged()
    {
        if (_result is not null) ApplyResult(_result);
        else RefreshFilePresence();
        OnPropertyChanged(nameof(SectionTitle));
        OnPropertyChanged(nameof(NetworkNotice));
        OnPropertyChanged(nameof(RequirementsLabel));
        OnPropertyChanged(nameof(InterpreterLabel));
        OnPropertyChanged(nameof(CheckText));
        OnPropertyChanged(nameof(InstallText));
        OnPropertyChanged(nameof(CancelText));
        OnPropertyChanged(nameof(DetailsTitle));
        OnPropertyChanged(nameof(CompatibilityTitle));
        OnPropertyChanged(nameof(CopyText));
        OnPropertyChanged(nameof(RisksTitle));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        StopWatcher();
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}

public sealed class DependencyItemRowViewModel
{
    public string Name { get; }
    public string Requested { get; }
    public string InstalledVersion { get; }
    public string PlannedVersion { get; }
    public string Status { get; }
    public string Source { get; }

    public DependencyItemRowViewModel(DependencyItem item, ILocalizationService localization)
    {
        Name = item.Name;
        Requested = item.Requested;
        InstalledVersion = item.InstalledVersion ?? localization["Value_None"];
        PlannedVersion = item.PlannedVersion;
        Status = localization[item.Status == DependencyItemStatus.Missing
            ? "Dependency_Item_Missing"
            : "Dependency_Item_VersionMismatch"];
        Source = item.DisplaySource ?? string.Empty;
    }
}
