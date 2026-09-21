using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PyRunner.Models;
using PyRunner.Services;
using PyRunner.Services.AI;

namespace PyRunner.ViewModels;

public sealed partial class AiChatMessageViewModel : ObservableObject
{
    public string Id { get; }
    public AiMessageRole Role { get; }
    public AiMessageKind Kind { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public string? RequestId { get; }
    [ObservableProperty] private string content;

    public AiChatMessageViewModel(string id, AiMessageRole role, AiMessageKind kind, string content,
        DateTimeOffset createdAtUtc, string? requestId = null)
    {
        Id = id; Role = role; Kind = kind; this.content = content;
        CreatedAtUtc = createdAtUtc; RequestId = requestId;
    }

    public AiStoredMessage ToStored() => new(Id, Role, Kind, Content, CreatedAtUtc, RequestId);
}

public sealed class AiContextFileViewModel
{
    public AiContextFile Model { get; }
    public string DisplayName => Model.DisplayName;
    public string SizeText => Model.SizeBytes < 1024 ? $"{Model.SizeBytes} B" : $"{Model.SizeBytes / 1024d:0.#} KB";
    public bool IsPrimary => Model.IsPrimary;
    public AiContextFileViewModel(AiContextFile model) => Model = model;
}

public sealed class AiConversationSummaryViewModel
{
    public AiConversationSummary Model { get; }
    public string Id => Model.Id;
    public string Title => Model.Title;
    public string Meta => $"{Model.UpdatedAtUtc.ToLocalTime():g} · {Model.MessageCount}";
    public AiConversationSummaryViewModel(AiConversationSummary model) => Model = model;
}

public sealed partial class AiAssistantViewModel : ObservableObject, IDisposable
{
    private const int MaximumContextFiles = 8;
    private readonly AiConfigurationService _configuration;
    private readonly IAiCredentialStore _credentials;
    private readonly IAiProviderClient _provider;
    private readonly IAiContextBuilder _contextBuilder;
    private readonly ISecretScanService _secretScanner;
    private readonly IAiChangeReviewService _changes;
    private readonly IAiInteractionService _interaction;
    private readonly IAiConversationStore _conversationStore;
    private readonly IAiFileContextService _fileContexts;
    private readonly IAiPatchApplicationService _patches;
    private readonly ILocalizationService _localization;
    private IAiEditorBridge? _editor;
    private CancellationTokenSource? _requestCancellation;
    private AiPatchProposal? _proposal;
    private string _conversationId = Guid.NewGuid().ToString();
    private DateTimeOffset _createdAtUtc = DateTimeOffset.UtcNow;
    private string _conversationTitle = string.Empty;
    private string? _lastPrompt;
    private string? _suppressedPrimaryPath;
    private bool _disposed;

    [ObservableProperty] private bool isOpen;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isHistoryOpen;
    [ObservableProperty] private bool hasProposal;
    [ObservableProperty] private bool hasFailure;
    [ObservableProperty] private string promptText = string.Empty;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private string usageText = string.Empty;
    [ObservableProperty] private int selectedModeIndex;
    [ObservableProperty] private AiConversationSummaryViewModel? selectedHistory;

    public ObservableCollection<AiChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<AiContextFileViewModel> ContextFiles { get; } = [];
    public ObservableCollection<AiConversationSummaryViewModel> History { get; } = [];
    public ObservableCollection<string> ModeOptions { get; } = [];
    public ObservableCollection<string> ModelOptions { get; } = [];

    public IAsyncRelayCommand SendCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IRelayCommand NewConversationCommand { get; }
    public IRelayCommand ToggleHistoryCommand { get; }
    public IAsyncRelayCommand OpenHistoryCommand { get; }
    public IAsyncRelayCommand DeleteHistoryCommand { get; }
    public IAsyncRelayCommand ViewDiffCommand { get; }
    public IAsyncRelayCommand ApplyProposalCommand { get; }
    public IRelayCommand DiscardProposalCommand { get; }
    public IRelayCommand<string> QuickPromptCommand { get; }
    public IAsyncRelayCommand<AiContextFileViewModel> RemoveContextCommand { get; }

    public event EventHandler? MessagesChanged;
    public event EventHandler? PromptFocusRequested;

    public string PageTitle => _localization["AI_Page_Title"];
    public string PageSubtitle => _localization["AI_Page_Subtitle"];
    public string HistoryText => _localization["AI_History"];
    public string NewConversationText => _localization["AI_NewConversation"];
    public string PromptPlaceholder => _localization["AI_ConversationPlaceholder"];
    public string AddContextText => _localization["AI_AddContext"];
    public string SendText => _localization["AI_Send"];
    public string StopText => _localization["AI_Stop"];
    public string ApplyText => _localization["AI_ApplyChanges"];
    public string ViewDiffText => _localization["AI_ViewDiff"];
    public string DiscardText => _localization["AI_DiscardChanges"];
    public string DeleteText => _localization["AI_DeleteConversation"];
    public string HistoryOpenText => _localization["AI_OpenConversation"];
    public string CopyText => _localization["AI_Copy"];
    public string RetryText => _localization["AI_Retry"];
    public string CodeFileFallback => _localization["AI_CodeFileFallback"];
    public string RemoveContextText => _localization["AI_RemoveContext"];
    public string ContextSummary => string.Format(_localization["AI_ContextSummary"], ContextFiles.Count,
        ContextFiles.Sum(item => item.Model.SizeBytes) / 1024d);
    public string CloseText => _localization["Button_Close"];
    public string EmptyTitle => _localization["AI_EmptyTitle"];
    public string EmptyDescription => _localization["AI_EmptyDescription"];
    public string ExplainText => _localization["AI_Explain"];
    public string RefactorText => _localization["AI_Refactor"];
    public string OptimizeText => _localization["AI_Optimize"];
    public string DiagnoseText => _localization["AI_Diagnose"];
    public string GenerateText => _localization["AI_Generate"];
    public string CurrentModel => _configuration.GetCurrent().Model;
    public bool IsEmpty => Messages.Count == 0;
    public bool HasContext => ContextFiles.Count > 0;
    public bool CanSend => !IsBusy && !string.IsNullOrWhiteSpace(PromptText) && HasContext;
    public AiPermissionMode SelectedMode => SelectedModeIndex == 1
        ? AiPermissionMode.GenerateChanges : AiPermissionMode.ReadOnlyAnalysis;
    public string ProposalTitle => _proposal is null ? string.Empty :
        string.Format(_localization["AI_ProposalTitle"], _proposal.Changes.Count);
    public string ProposalStats => _proposal is null ? string.Empty :
        string.Format(_localization["AI_ProposalStats"], _proposal.AddedLines, _proposal.DeletedLines);
    public int ProposalAddedLines => _proposal?.AddedLines ?? 0;
    public int ProposalDeletedLines => _proposal?.DeletedLines ?? 0;
    public string ProposalSummary => _proposal?.Summary ?? string.Empty;

    public AiAssistantViewModel(AiConfigurationService configuration, IAiCredentialStore credentials,
        IAiProviderClient provider, IAiContextBuilder contextBuilder, ISecretScanService secretScanner,
        IAiChangeReviewService changes, IAiInteractionService interaction, IAiConversationStore conversationStore,
        IAiFileContextService fileContexts, IAiPatchApplicationService patches, ILocalizationService localization)
    {
        _configuration = configuration; _credentials = credentials; _provider = provider;
        _contextBuilder = contextBuilder; _secretScanner = secretScanner; _changes = changes;
        _interaction = interaction; _conversationStore = conversationStore; _fileContexts = fileContexts;
        _patches = patches; _localization = localization;
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => HasFailure && !IsBusy);
        NewConversationCommand = new RelayCommand(NewConversation);
        ToggleHistoryCommand = new RelayCommand(() => IsHistoryOpen = !IsHistoryOpen);
        OpenHistoryCommand = new AsyncRelayCommand(OpenSelectedHistoryAsync, () => SelectedHistory is not null && !IsBusy);
        DeleteHistoryCommand = new AsyncRelayCommand(DeleteSelectedHistoryAsync, () => SelectedHistory is not null && !IsBusy);
        ViewDiffCommand = new AsyncRelayCommand(ViewDiffAsync, () => HasProposal && !IsBusy);
        ApplyProposalCommand = new AsyncRelayCommand(ApplyProposalAsync, () => HasProposal && !IsBusy);
        DiscardProposalCommand = new RelayCommand(DiscardProposal, () => HasProposal && !IsBusy);
        QuickPromptCommand = new RelayCommand<string>(ApplyQuickPrompt);
        RemoveContextCommand = new AsyncRelayCommand<AiContextFileViewModel>(RemoveContextAsync,
            file => file is not null && !IsBusy);
        _localization.LanguageChanged += OnLanguageChanged;
        RefreshLocalizedCollections();
        RefreshHistory();
        StatusText = _localization["AI_Status_Idle"];
    }

    public void AttachEditor(IAiEditorBridge editor) => _editor = editor;

    public async Task ExecutePrimaryActionAsync()
    {
        if (IsBusy) Cancel();
        else await SendAsync();
    }

    public Task RetryLastAsync() => RetryAsync();

    public async Task RestoreLatestConversationAsync()
    {
        if (Messages.Count > 0 || History.Count == 0) return;
        SelectedHistory = History[0];
        await OpenSelectedHistoryAsync();
        SelectedHistory = null;
    }

    public async Task SynchronizeCurrentFileAsync(CancellationToken cancellationToken = default)
    {
        var path = _editor?.CurrentFilePath;
        if (string.IsNullOrWhiteSpace(path)) return;
        if (_suppressedPrimaryPath is not null)
        {
            if (_suppressedPrimaryPath.Equals(path, StringComparison.OrdinalIgnoreCase)) return;
            _suppressedPrimaryPath = null;
        }
        try
        {
            var current = await _fileContexts.LoadAsync(path, true, cancellationToken);
            var oldPrimary = ContextFiles.FirstOrDefault(item => item.IsPrimary);
            if (oldPrimary is not null && oldPrimary.Model.FullPath.Equals(current.FullPath, StringComparison.OrdinalIgnoreCase) &&
                oldPrimary.Model.Sha256.Equals(current.Sha256, StringComparison.OrdinalIgnoreCase)) return;
            if (oldPrimary is not null) ContextFiles.Remove(oldPrimary);
            var duplicate = ContextFiles.FirstOrDefault(item => item.Model.FullPath.Equals(current.FullPath, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null) ContextFiles.Remove(duplicate);
            ContextFiles.Insert(0, new(current));
            NotifyContextChanged();
            Persist();
        }
        catch { StatusText = _localization["AI_Error_ContextLoadFailed"]; }
    }

    public async Task AddContextFilesAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        foreach (var path in paths)
        {
            if (ContextFiles.Count >= MaximumContextFiles) break;
            if (ContextFiles.Any(item => item.Model.FullPath.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) continue;
            try { ContextFiles.Add(new(await _fileContexts.LoadAsync(path, false, cancellationToken))); }
            catch { StatusText = _localization["AI_Error_ContextLoadFailed"]; }
        }
        NotifyContextChanged();
        Persist();
    }

    private async Task SendAsync()
    {
        if (_disposed || !CanSend) return;
        await SynchronizeCurrentFileAsync();
        if (!HasContext) return;
        var prompt = PromptText.Trim();
        var requestMode = SelectedMode;
        try
        {
            if (!_configuration.IsEnabled)
                throw new AiProviderException(AiErrorKind.InvalidConfiguration, "AI_Error_Disabled");
            var configuration = _configuration.GetCurrent();
            var credential = _credentials.Retrieve(new(configuration.Provider, configuration.Endpoint.Authority));
            if (credential is null)
                throw new AiProviderException(AiErrorKind.MissingCredential, "AI_Error_MissingCredential");
            var history = Messages.Where(message => message.Kind == AiMessageKind.Text &&
                                                    message.Role is AiMessageRole.User or AiMessageRole.Assistant)
                .TakeLast(20).Select(message => new AiConversationTurn(
                    message.Role == AiMessageRole.Assistant ? "assistant" : "user", message.Content)).ToArray();
            if (requestMode == AiPermissionMode.GenerateChanges &&
                ContextFiles.Select(item => item.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != ContextFiles.Count)
                throw new AiProviderException(AiErrorKind.InvalidConfiguration, "AI_Error_AmbiguousContextNames");
            var request = _contextBuilder.BuildConversationRequest(requestMode,
                ContextFiles.Select(item => item.Model).ToArray(), history, prompt);
            var previewText = request.Context + "\n\n" + prompt;
            var findings = _secretScanner.Scan(previewText);
            var preview = new AiSendPreview(configuration.Endpoint.Authority, configuration.Model,
                string.Join(", ", ContextFiles.Select(item => item.DisplayName)), previewText.Length,
                Math.Max(1, (int)Math.Ceiling(previewText.Length / 4d)), findings);
            StatusText = _localization["AI_Status_AwaitingConfirmation"];
            if (!await _interaction.ConfirmSendAsync(preview,
                    _secretScanner.CreateMaskedPreview(previewText, findings), CancellationToken.None))
            {
                StatusText = _localization["AI_Status_Cancelled"];
                return;
            }

            _lastPrompt = prompt;
            HasFailure = false;
            ClearProposal();
            if (Messages.Count == 0) _conversationTitle = CreateTitle(prompt);
            AddMessage(AiMessageRole.User, AiMessageKind.Text, prompt);
            PromptText = string.Empty;
            AddMessage(AiMessageRole.System, AiMessageKind.AnalysisStatus,
                string.Format(_localization["AI_Status_ReadingFiles"], ContextFiles.Count));
            var assistant = AddMessage(AiMessageRole.Assistant, AiMessageKind.Text, string.Empty);
            IsBusy = true;
            StatusText = string.Format(_localization["AI_Status_ReadingFiles"], ContextFiles.Count);
            NotifyCommands();
            _requestCancellation = new CancellationTokenSource();
            var progress = new Progress<AiStreamEvent>(item =>
            {
                assistant.Content += item.Delta;
                StatusText = _localization["AI_Status_Streaming"];
            });
            var result = await _provider.StreamAsync(configuration, credential, request, progress, _requestCancellation.Token);
            assistant.Content = result.Text;
            if (requestMode == AiPermissionMode.GenerateChanges)
            {
                var candidate = _changes.ParseCandidate(result.Text)
                    ?? throw new AiProviderException(AiErrorKind.InvalidResponse, "AI_Error_InvalidStructuredResponse", result.RequestId);
                _proposal = BuildProposal(candidate);
                assistant.Content = BuildProposalMarkdown(candidate);
                SetProposalState(true);
                StatusText = _localization["AI_Status_Reviewable"];
            }
            else StatusText = _localization["AI_Status_Completed"];
            UsageText = result.InputTokens is null && result.OutputTokens is null ? string.Empty :
                string.Format(_localization["AI_Usage"], result.InputTokens ?? 0, result.OutputTokens ?? 0);
            Persist();
        }
        catch (AiProviderException ex)
        {
            HasFailure = ex.Kind is not AiErrorKind.Cancelled;
            var message = ex.Kind == AiErrorKind.Cancelled ? _localization["AI_Status_Cancelled"] : ResolveError(ex);
            AddMessage(AiMessageRole.System,
                ex.Kind == AiErrorKind.Cancelled ? AiMessageKind.Cancelled : AiMessageKind.Error, message, ex.RequestId);
            StatusText = message;
            Persist();
        }
        catch
        {
            HasFailure = true;
            AddMessage(AiMessageRole.System, AiMessageKind.Error, _localization["AI_Error_RequestFailed"]);
            StatusText = _localization["AI_Error_RequestFailed"];
            Persist();
        }
        finally
        {
            IsBusy = false;
            _requestCancellation?.Dispose();
            _requestCancellation = null;
            NotifyCommands();
        }
    }

    private AiPatchProposal BuildProposal(AiCodeCandidate candidate)
    {
        var pending = new List<AiPendingFileChange>();
        foreach (var file in candidate.EffectiveFiles)
        {
            AiContextFileViewModel? context;
            if (string.IsNullOrWhiteSpace(file.FileName) && ContextFiles.Count == 1) context = ContextFiles[0];
            else context = ContextFiles.SingleOrDefault(item => item.DisplayName.Equals(file.FileName, StringComparison.OrdinalIgnoreCase));
            if (context is null) throw new AiProviderException(AiErrorKind.InvalidResponse, "AI_Error_InvalidPatch");
            pending.Add(new(context.Model.FullPath, context.DisplayName, context.Model.Sha256, context.Model.Content,
                file.Code, context.Model.EncodingName, context.Model.HasBom, context.Model.NewLine, file.Summary));
        }
        var added = 0; var deleted = 0; var diff = new StringBuilder();
        foreach (var change in pending)
        {
            var local = _changes.CreateLineDiff(change.OriginalText, change.NewText);
            added += local.Split('\n').Count(line => line.StartsWith("+ ", StringComparison.Ordinal));
            deleted += local.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal));
            diff.Append("--- ").AppendLine(change.DisplayName).Append(local).AppendLine();
        }
        return new(candidate.Summary, candidate.Notes, pending, added, deleted, diff.ToString());
    }

    private static string BuildProposalMarkdown(AiCodeCandidate candidate)
    {
        var output = new StringBuilder().Append("## ").AppendLine(candidate.Summary).AppendLine();
        if (!string.IsNullOrWhiteSpace(candidate.Notes)) output.AppendLine(candidate.Notes).AppendLine();
        foreach (var file in candidate.EffectiveFiles)
            output.Append("```python ").AppendLine(file.FileName).AppendLine(file.Code).AppendLine("```");
        return output.ToString();
    }

    private async Task ApplyProposalAsync()
    {
        if (_proposal is null || IsBusy) return;
        if (_editor?.HasUnsavedChanges == true)
        {
            StatusText = _localization["AI_Error_UnsavedEditor"];
            AddMessage(AiMessageRole.System, AiMessageKind.Error, StatusText);
            return;
        }
        var candidate = new AiCodeCandidate(_proposal.Summary, _proposal.Changes[0].NewText, _proposal.Notes,
            _proposal.Changes.Select(change => new AiFileChange(change.DisplayName, change.NewText, change.Summary)).ToArray());
        if (!await _interaction.ConfirmApplyAsync(candidate, _proposal.Diff, CancellationToken.None)) return;
        IsBusy = true; NotifyCommands();
        try
        {
            var result = await _patches.ApplyAsync(_proposal.Changes);
            if (!result.Succeeded)
            {
                StatusText = _localization[result.ErrorKey];
                AddMessage(AiMessageRole.System, AiMessageKind.Error, StatusText);
                return;
            }
            StatusText = string.Format(_localization["AI_Status_AppliedWithBackup"], result.BackupDirectory ?? string.Empty);
            AddMessage(AiMessageRole.System, AiMessageKind.AnalysisStatus, StatusText);
            ClearProposal();
            if (_editor is not null) await _editor.ReloadFromDiskAsync();
            await ReloadContextsAsync();
            Persist();
        }
        finally { IsBusy = false; NotifyCommands(); }
    }

    private Task ViewDiffAsync() => _proposal is null
        ? Task.CompletedTask : _interaction.ShowDiffAsync(_proposal.Diff, CancellationToken.None);

    private void DiscardProposal()
    {
        ClearProposal();
        StatusText = _localization["AI_Status_ProposalDiscarded"];
        AddMessage(AiMessageRole.System, AiMessageKind.Cancelled, StatusText);
        Persist();
    }

    private async Task RetryAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastPrompt)) return;
        PromptText = _lastPrompt;
        await SendAsync();
    }

    private void NewConversation()
    {
        Cancel(); Persist();
        foreach (var message in Messages) message.PropertyChanged -= OnMessagePropertyChanged;
        Messages.Clear();
        ClearProposal();
        HasFailure = false; UsageText = string.Empty; PromptText = string.Empty;
        _conversationId = Guid.NewGuid().ToString(); _createdAtUtc = DateTimeOffset.UtcNow;
        _conversationTitle = string.Empty; _lastPrompt = null; IsHistoryOpen = false;
        StatusText = _localization["AI_Status_Idle"];
        NotifyMessagesChanged(); RefreshHistory(); PromptFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenSelectedHistoryAsync()
    {
        if (SelectedHistory is null) return;
        Persist();
        var stored = _conversationStore.Load(SelectedHistory.Id);
        if (stored is null) return;
        foreach (var message in Messages) message.PropertyChanged -= OnMessagePropertyChanged;
        Messages.Clear(); ContextFiles.Clear(); ClearProposal();
        _conversationId = stored.Id; _createdAtUtc = stored.CreatedAtUtc; _conversationTitle = stored.Title;
        SelectedModeIndex = stored.Mode == AiPermissionMode.GenerateChanges ? 1 : 0;
        foreach (var item in stored.Messages) AddMessage(item.Role, item.Kind, item.Content, item.RequestId, item.Id, item.CreatedAtUtc);
        foreach (var path in stored.ContextPaths.Take(MaximumContextFiles))
        {
            try { ContextFiles.Add(new(await _fileContexts.LoadAsync(path, ContextFiles.Count == 0))); } catch { }
        }
        IsHistoryOpen = false; HasFailure = Messages.LastOrDefault()?.Kind == AiMessageKind.Error;
        NotifyContextChanged(); NotifyMessagesChanged();
    }

    private async Task DeleteSelectedHistoryAsync()
    {
        if (SelectedHistory is null || !await _interaction.ConfirmDeleteConversationAsync(
                SelectedHistory.Title, CancellationToken.None)) return;
        var deletedCurrent = SelectedHistory.Id == _conversationId;
        _conversationStore.Delete(SelectedHistory.Id);
        SelectedHistory = null;
        if (deletedCurrent) NewConversation(); else RefreshHistory();
    }

    private Task RemoveContextAsync(AiContextFileViewModel? file)
    {
        if (file is not null)
        {
            if (file.IsPrimary) _suppressedPrimaryPath = file.Model.FullPath;
            ContextFiles.Remove(file); NotifyContextChanged(); Persist();
        }
        return Task.CompletedTask;
    }

    private async Task ReloadContextsAsync()
    {
        var paths = ContextFiles.Select(item => (item.Model.FullPath, item.IsPrimary)).ToArray();
        ContextFiles.Clear();
        foreach (var (path, primary) in paths)
        {
            try { ContextFiles.Add(new(await _fileContexts.LoadAsync(path, primary))); } catch { }
        }
        NotifyContextChanged();
    }

    private void ApplyQuickPrompt(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        PromptText = _localization[key];
        PromptFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private AiChatMessageViewModel AddMessage(AiMessageRole role, AiMessageKind kind, string content,
        string? requestId = null, string? id = null, DateTimeOffset? createdAt = null)
    {
        var message = new AiChatMessageViewModel(id ?? Guid.NewGuid().ToString(), role, kind, content,
            createdAt ?? DateTimeOffset.UtcNow, requestId);
        message.PropertyChanged += OnMessagePropertyChanged;
        Messages.Add(message); NotifyMessagesChanged(); return message;
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e) => NotifyMessagesChanged();
    private void NotifyMessagesChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        MessagesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyContextChanged()
    {
        OnPropertyChanged(nameof(HasContext)); OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(ContextSummary));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void SetProposalState(bool value)
    {
        HasProposal = value;
        OnPropertyChanged(nameof(ProposalTitle)); OnPropertyChanged(nameof(ProposalStats));
        OnPropertyChanged(nameof(ProposalAddedLines)); OnPropertyChanged(nameof(ProposalDeletedLines));
        OnPropertyChanged(nameof(ProposalSummary)); NotifyCommands(); NotifyMessagesChanged();
    }
    private void ClearProposal() { _proposal = null; SetProposalState(false); }
    private void Cancel() => _requestCancellation?.Cancel();

    private void Persist()
    {
        if (Messages.Count == 0) return;
        try
        {
            _conversationStore.Save(new(_conversationId,
                string.IsNullOrWhiteSpace(_conversationTitle) ? _localization["AI_UntitledConversation"] : _conversationTitle,
                SelectedMode, _createdAtUtc, DateTimeOffset.UtcNow,
                Messages.Select(message => message.ToStored()).ToArray(),
                ContextFiles.Select(item => item.Model.FullPath).ToArray()));
            RefreshHistory();
        }
        catch { }
    }

    private void RefreshHistory()
    {
        try
        {
            History.Clear();
            foreach (var item in _conversationStore.List()) History.Add(new(item));
        }
        catch { }
    }

    private static string CreateTitle(string prompt) => prompt[..Math.Min(prompt.Length, 48)];
    private string ResolveError(AiProviderException exception)
    {
        var text = _localization[exception.Message];
        if (exception.Kind == AiErrorKind.RateLimited && exception.RetryAfter is { } retry)
            text += " " + string.Format(_localization["AI_RetryAfter"], Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)));
        if (!string.IsNullOrWhiteSpace(exception.RequestId))
            text += " " + string.Format(_localization["AI_RequestId"], exception.RequestId);
        return text;
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanSend));
        SendCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); RetryCommand.NotifyCanExecuteChanged();
        OpenHistoryCommand.NotifyCanExecuteChanged(); DeleteHistoryCommand.NotifyCanExecuteChanged();
        ViewDiffCommand.NotifyCanExecuteChanged(); ApplyProposalCommand.NotifyCanExecuteChanged();
        DiscardProposalCommand.NotifyCanExecuteChanged(); RemoveContextCommand.NotifyCanExecuteChanged();
    }

    partial void OnPromptTextChanged(string value) { OnPropertyChanged(nameof(CanSend)); SendCommand.NotifyCanExecuteChanged(); }
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnHasFailureChanged(bool value) => RetryCommand.NotifyCanExecuteChanged();
    partial void OnSelectedHistoryChanged(AiConversationSummaryViewModel? value)
    { OpenHistoryCommand.NotifyCanExecuteChanged(); DeleteHistoryCommand.NotifyCanExecuteChanged(); }
    partial void OnSelectedModeIndexChanged(int value) { OnPropertyChanged(nameof(SelectedMode)); Persist(); }

    private void RefreshLocalizedCollections()
    {
        ModeOptions.Clear(); ModeOptions.Add(_localization["AI_Mode_ReadOnly"]); ModeOptions.Add(_localization["AI_Mode_Generate"]);
        ModelOptions.Clear(); ModelOptions.Add(CurrentModel);
    }

    private void OnLanguageChanged()
    {
        RefreshLocalizedCollections();
        foreach (var property in new[] { nameof(PageTitle), nameof(PageSubtitle), nameof(HistoryText),
                     nameof(NewConversationText), nameof(PromptPlaceholder), nameof(AddContextText), nameof(SendText),
                     nameof(StopText), nameof(ApplyText), nameof(ViewDiffText), nameof(DiscardText), nameof(DeleteText),
                     nameof(HistoryOpenText), nameof(CopyText), nameof(RetryText), nameof(CodeFileFallback), nameof(RemoveContextText),
                     nameof(ContextSummary),
                     nameof(CloseText), nameof(EmptyTitle), nameof(EmptyDescription), nameof(ExplainText),
                     nameof(RefactorText), nameof(OptimizeText), nameof(DiagnoseText), nameof(GenerateText),
                     nameof(ProposalTitle), nameof(ProposalStats) }) OnPropertyChanged(property);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; Cancel(); Persist();
        foreach (var message in Messages) message.PropertyChanged -= OnMessagePropertyChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
    }
}
