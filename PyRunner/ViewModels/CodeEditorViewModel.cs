using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml.Controls;
using PyRunner.Editor;
using PyRunner.Models;
using PyRunner.Services;
using PyRunner.Services.AI;

namespace PyRunner.ViewModels;

public sealed partial class CodeEditorViewModel : ObservableObject, IDisposable, IAiEditorBridge
{
    private const int ProtocolVersion = 1;
    private const string VirtualHostName = "pyrunner-editor.local";
    private readonly ICodeDocumentService _documents;
    private readonly IRecoveryDraftService _drafts;
    private readonly IExternalEditorLauncher _externalEditor;
    private readonly ICodeEditorInteraction _interaction;
    private readonly ILocalizationService _localization;
    private WebView2? _webView;
    private CoreWebView2? _core;
    private CodeDocument? _document;
    private TaskCompletionSource<string?>? _textRequest;
    private string? _textRequestId;
    private TaskCompletionSource<AiEditorContext?>? _aiContextRequest;
    private string? _aiContextRequestId;
    private TaskCompletionSource<bool>? _aiApplyRequest;
    private string? _aiApplyRequestId;
    private string? _pendingText;
    private bool _pendingDirty;
    private CancellationTokenSource? _draftDelay;
    private bool _pageReady;
    private bool _disposed;
    private bool _isLightTheme;
    private bool _isUntitled;

    [ObservableProperty] private bool hasDocument;
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isReadOnly;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private string encodingText = string.Empty;
    [ObservableProperty] private string newLineText = string.Empty;
    [ObservableProperty] private string positionText = "1:1";
    [ObservableProperty] private string filePath = string.Empty;

    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand UndoCommand { get; }
    public IRelayCommand RedoCommand { get; }
    public IAsyncRelayCommand OpenExternalCommand { get; }

    public string SaveText => _localization["Editor_Save"];
    public string UndoText => _localization["Editor_Undo"];
    public string RedoText => _localization["Editor_Redo"];
    public string ExternalText => _localization["Editor_OpenExternal"];
    public string ReadOnlyText => IsReadOnly ? _localization["Editor_ReadOnly"] : string.Empty;
    public string? CurrentFilePath => _document?.Fingerprint.Path;
    public bool HasUnsavedChanges => IsDirty;

    public CodeEditorViewModel(
        ICodeDocumentService documents,
        IRecoveryDraftService drafts,
        IExternalEditorLauncher externalEditor,
        ICodeEditorInteraction interaction,
        ILocalizationService localization)
    {
        _documents = documents;
        _drafts = drafts;
        _externalEditor = externalEditor;
        _interaction = interaction;
        _localization = localization;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);
        UndoCommand = new RelayCommand(() => Post(new { type = "undo", version = ProtocolVersion }), () => CanEdit);
        RedoCommand = new RelayCommand(() => Post(new { type = "redo", version = ProtocolVersion }), () => CanEdit);
        OpenExternalCommand = new AsyncRelayCommand(OpenExternalAsync, () => HasDocument && !IsBusy);
        _localization.LanguageChanged += OnLanguageChanged;
        StatusText = _localization["Editor_Status_NoDocument"];
    }

    public bool CanEdit => HasDocument && !IsReadOnly && !IsBusy && _pageReady;
    public bool CanSave => CanEdit && IsDirty;

    public async Task InitializeAsync(WebView2 webView)
    {
        if (_disposed || _webView is not null) return;
        _webView = webView;
        try
        {
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PyRunner", "WebView2"),
                EnvironmentVariableTarget.Process);
            await webView.EnsureCoreWebView2Async();
            if (_disposed) return;
            _core = webView.CoreWebView2;
            _core.Settings.AreDefaultContextMenusEnabled = false;
            _core.Settings.AreDevToolsEnabled = false;
            _core.Settings.IsStatusBarEnabled = false;
            _core.SetVirtualHostNameToFolderMapping(VirtualHostName,
                Path.Combine(AppContext.BaseDirectory, "Editor", "wwwroot"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            _core.WebMessageReceived += OnMessage;
            _core.NavigationStarting += OnNavigationStarting;
            _core.Navigate($"https://{VirtualHostName}/index.html");
        }
        catch
        {
            HasError = true;
            StatusText = _localization["Editor_Error_WebView"];
        }
        NotifyCommands();
    }

    public async Task<bool> PrepareToSwitchAsync(CancellationToken cancellationToken = default)
    {
        if (!IsDirty) return true;
        var choice = await _interaction.ConfirmUnsavedChangesAsync(cancellationToken);
        if (choice == UnsavedChangesChoice.Cancel) return false;
        if (choice == UnsavedChangesChoice.Save)
        {
            await SaveAsync();
            return !IsDirty;
        }
        if (_document is not null) _drafts.Delete(_document.Fingerprint.Path);
        IsDirty = false;
        if (_document is not null)
        {
            _pendingText = _document.Text;
            _pendingDirty = false;
            SendDocument();
        }
        return true;
    }

    public async Task LoadAsync(string? scriptPath, CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            _document = null;
            _pendingText = null;
            HasDocument = false;
            IsDirty = false;
            StatusText = _localization["Editor_Status_NoDocument"];
            NotifyCommands();
            return;
        }

        IsBusy = true;
        HasError = false;
        try
        {
            var document = await _documents.LoadAsync(scriptPath, cancellationToken);
            _document = document;
            _isUntitled = false;
            FilePath = document.Fingerprint.Path;
            HasDocument = true;
            IsReadOnly = document.IsReadOnly;
            EncodingText = document.HasBom ? $"{document.EncodingName} BOM" : document.EncodingName;
            NewLineText = document.NewLine == "\r\n" ? "CRLF" : document.NewLine == "\n" ? "LF" : "CR";
            StatusText = document.IsReadOnly
                ? _localization[document.ReadOnlyReason]
                : _localization["Editor_Status_Ready"];

            var text = document.Text;
            var draft = await _drafts.LoadAsync(document.Fingerprint.Path, cancellationToken);
            if (draft is not null && draft.BaseSha256 == document.Fingerprint.Sha256 &&
                !string.Equals(draft.Text, document.Text, StringComparison.Ordinal) &&
                await _interaction.ConfirmRestoreDraftAsync(draft.SavedAtUtc, cancellationToken))
            {
                text = draft.Text;
                IsDirty = true;
                _pendingDirty = true;
                StatusText = _localization["Editor_Status_DraftRestored"];
            }
            else
            {
                IsDirty = false;
                _pendingDirty = false;
            }
            _pendingText = text;
            SendDocument();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            _document = null;
            HasDocument = false;
            IsReadOnly = true;
            HasError = true;
            StatusText = ResolveError(ex.Message);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    public async Task CheckExternalChangeAsync(CancellationToken cancellationToken = default)
    {
        if (_document is null || IsBusy) return;
        if (await _documents.IsCurrentAsync(_document.Fingerprint, cancellationToken)) return;
        StatusText = _localization["Editor_Status_ExternalChange"];
        if (!IsDirty) await LoadAsync(_document.Fingerprint.Path, cancellationToken);
    }

    public async Task<AiEditorContext?> RequestAiContextAsync(CancellationToken cancellationToken = default)
    {
        if (!HasDocument || !_pageReady || _document is null) return null;
        var completion = new TaskCompletionSource<AiEditorContext?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _aiContextRequest?.TrySetResult(null);
        _aiContextRequest = completion;
        _aiContextRequestId = Guid.NewGuid().ToString("N");
        Post(new { type = "requestAiContext", version = ProtocolVersion, requestId = _aiContextRequestId });
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
        catch { return null; }
        finally
        {
            if (ReferenceEquals(_aiContextRequest, completion)) { _aiContextRequest = null; _aiContextRequestId = null; }
        }
    }

    public async Task<bool> ApplyAiCandidateAsync(AiEditorContext original, string candidate,
        CancellationToken cancellationToken = default)
    {
        if (!CanEdit || candidate.Length > CodeDocumentService.EditableSizeLimit) return false;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _aiApplyRequest?.TrySetResult(false);
        _aiApplyRequest = completion;
        _aiApplyRequestId = Guid.NewGuid().ToString("N");
        Post(new
        {
            type = "applyAiCandidate", version = ProtocolVersion, requestId = _aiApplyRequestId,
            start = original.SelectionStart, end = original.SelectionEnd,
            expected = original.Text, candidate,
        });
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); }
        catch { return false; }
        finally
        {
            if (ReferenceEquals(_aiApplyRequest, completion)) { _aiApplyRequest = null; _aiApplyRequestId = null; }
        }
    }

    public async Task ReloadFromDiskAsync(CancellationToken cancellationToken = default)
    {
        if (_document is null || IsDirty) return;
        await LoadAsync(_document.Fingerprint.Path, cancellationToken);
    }

    public void OpenGeneratedDraft(string code)
    {
        if (_disposed || code.Length > CodeDocumentService.EditableSizeLimit) return;
        var bytes = Encoding.UTF8.GetBytes(code);
        _document = new CodeDocument(code, "utf-8", false, "\r\n", false, string.Empty,
            new CodeDocumentFingerprint("GeneratedScript.py", bytes.LongLength, DateTimeOffset.UtcNow,
                Convert.ToHexString(SHA256.HashData(bytes))));
        _isUntitled = true;
        FilePath = _localization["AI_GeneratedDraftName"];
        HasDocument = true;
        IsReadOnly = false;
        IsDirty = true;
        EncodingText = "utf-8";
        NewLineText = "CRLF";
        StatusText = _localization["AI_Status_GeneratedDraft"];
        _pendingText = code;
        _pendingDirty = true;
        SendDocument();
        NotifyCommands();
    }

    public void ApplyTheme(bool isLight)
    {
        _isLightTheme = isLight;
        Post(new { type = "theme", version = ProtocolVersion, theme = isLight ? "light" : "dark" });
    }

    private async Task SaveAsync()
    {
        if (!CanSave || _document is null) return;
        IsBusy = true;
        NotifyCommands();
        try
        {
            var text = await RequestTextAsync();
            if (text is null) { StatusText = _localization["Editor_Error_Bridge"]; return; }
            CodeDocumentSaveResult result;
            if (_isUntitled)
            {
                var path = await _interaction.PickSaveAsPathAsync("GeneratedScript.py");
                if (path is null) return;
                result = await _documents.SaveAsAsync(_document, path, text);
            }
            else result = await _documents.SaveAsync(_document, text, overwriteExternalChanges: false);
            if (result.Status == CodeDocumentSaveStatus.Conflict)
            {
                var choice = await _interaction.ResolveExternalConflictAsync();
                if (choice == ExternalConflictChoice.Reload)
                {
                    await LoadAsync(_document.Fingerprint.Path);
                    return;
                }
                if (choice == ExternalConflictChoice.SaveAs)
                {
                    var path = await _interaction.PickSaveAsPathAsync(_document.Fingerprint.Path);
                    if (path is null) return;
                    result = await _documents.SaveAsAsync(_document, path, text);
                }
                else if (choice == ExternalConflictChoice.Overwrite)
                    result = await _documents.SaveAsync(_document, text, overwriteExternalChanges: true);
                else return;
            }
            if (result.Status == CodeDocumentSaveStatus.Saved && result.Document is not null)
            {
                _document = result.Document;
                _isUntitled = false;
                FilePath = result.Document.Fingerprint.Path;
                IsDirty = false;
                _drafts.Delete(result.Document.Fingerprint.Path);
                Post(new { type = "markClean", version = ProtocolVersion });
                StatusText = _localization["Editor_Status_Saved"];
            }
            else
            {
                HasError = true;
                StatusText = _localization[result.ErrorKey];
            }
        }
        finally { IsBusy = false; NotifyCommands(); }
    }

    private async Task OpenExternalAsync()
    {
        if (_document is null) return;
        if (IsDirty && !await PrepareToSwitchAsync()) return;
        if (!await _externalEditor.OpenAsync(_document.Fingerprint.Path))
        {
            HasError = true;
            StatusText = _localization["Editor_Error_ExternalOpen"];
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 ||
            !uri.Host.Equals(VirtualHostName, StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.Equals("/index.html", StringComparison.Ordinal))
            args.Cancel = true;
    }

    private void OnMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var json = JsonDocument.Parse(args.WebMessageAsJson);
            var root = json.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() != ProtocolVersion ||
                !root.TryGetProperty("type", out var type)) return;
            switch (type.GetString())
            {
                case "ready":
                    _pageReady = true;
                    SendDocument();
                    break;
                case "state":
                    if (root.TryGetProperty("dirty", out var dirty) && dirty.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        IsDirty = dirty.GetBoolean();
                        if (IsDirty) ScheduleDraft();
                    }
                    if (root.TryGetProperty("line", out var line) && root.TryGetProperty("column", out var column) &&
                        line.TryGetInt32(out var lineNumber) && column.TryGetInt32(out var columnNumber) &&
                        lineNumber is > 0 and < 10_000_000 && columnNumber is > 0 and < 10_000_000)
                        PositionText = $"{lineNumber}:{columnNumber}";
                    break;
                case "text":
                    if (_textRequest is not null && root.TryGetProperty("requestId", out var requestId) &&
                        requestId.GetString() == _textRequestId && root.TryGetProperty("text", out var text) && text.GetString() is { } value &&
                        value.Length <= CodeDocumentService.EditableSizeLimit)
                        _textRequest.TrySetResult(value);
                    break;
                case "aiContext":
                    if (_aiContextRequest is not null && root.TryGetProperty("requestId", out var aiRequestId) &&
                        aiRequestId.GetString() == _aiContextRequestId && root.TryGetProperty("text", out var aiText) &&
                        aiText.GetString() is { } contextText && contextText.Length <= CodeDocumentService.EditableSizeLimit &&
                        root.TryGetProperty("selectionStart", out var startValue) && startValue.TryGetInt32(out var start) &&
                        root.TryGetProperty("selectionEnd", out var endValue) && endValue.TryGetInt32(out var end) &&
                        start >= 0 && end >= start && root.TryGetProperty("isSelection", out var selectionValue) &&
                        selectionValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contextText)));
                        _aiContextRequest.TrySetResult(new AiEditorContext(contextText,
                            Path.GetFileName(_document?.Fingerprint.Path) ?? "script.py",
                            selectionValue.GetBoolean(), start, end, sha,
                            FilePath: _document?.Fingerprint.Path));
                    }
                    break;
                case "aiApplyResult":
                    if (_aiApplyRequest is not null && root.TryGetProperty("requestId", out var applyRequestId) &&
                        applyRequestId.GetString() == _aiApplyRequestId && root.TryGetProperty("applied", out var applied) &&
                        applied.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        _aiApplyRequest.TrySetResult(applied.GetBoolean());
                    break;
                case "saveRequest":
                    if (SaveCommand.CanExecute(null)) _ = SaveCommand.ExecuteAsync(null);
                    break;
            }
            NotifyCommands();
        }
        catch { }
    }

    private void SendDocument()
    {
        if (!_pageReady || _document is null || _pendingText is null) return;
        Post(new
        {
            type = "loadDocument", version = ProtocolVersion, text = _pendingText,
            readOnly = _document?.IsReadOnly == true, theme = _isLightTheme ? "light" : "dark",
            dirty = _pendingDirty,
        });
    }

    private async Task<string?> RequestTextAsync()
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _textRequest?.TrySetResult(null);
        _textRequest = completion;
        _textRequestId = Guid.NewGuid().ToString("N");
        Post(new { type = "requestText", version = ProtocolVersion, requestId = _textRequestId });
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch { return null; }
        finally { if (ReferenceEquals(_textRequest, completion)) { _textRequest = null; _textRequestId = null; } }
    }

    private void ScheduleDraft()
    {
        _draftDelay?.Cancel();
        _draftDelay?.Dispose();
        var delay = new CancellationTokenSource();
        _draftDelay = delay;
        _ = SaveDraftAfterDelayAsync(delay);
    }

    private async Task SaveDraftAfterDelayAsync(CancellationTokenSource delay)
    {
        try
        {
            await Task.Delay(750, delay.Token);
            if (_document is null || !IsDirty) return;
            var text = await RequestTextAsync();
            if (text is null || delay.IsCancellationRequested) return;
            await _drafts.SaveAsync(new RecoveryDraft(_document.Fingerprint.Path, text,
                DateTimeOffset.UtcNow, _document.Fingerprint.Sha256), delay.Token);
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
        catch { }
        finally { if (ReferenceEquals(_draftDelay, delay)) { _draftDelay = null; delay.Dispose(); } }
    }

    private void Post(object payload)
    {
        try { _core?.PostWebMessageAsString(JsonSerializer.Serialize(payload)); } catch { }
    }

    private string ResolveError(string value) =>
        value.StartsWith("Editor_", StringComparison.Ordinal) ? _localization[value] : _localization["Editor_Error_LoadFailed"];

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ReadOnlyText));
        SaveCommand.NotifyCanExecuteChanged(); UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged();
        OpenExternalCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(SaveText)); OnPropertyChanged(nameof(UndoText)); OnPropertyChanged(nameof(RedoText));
        OnPropertyChanged(nameof(ExternalText)); OnPropertyChanged(nameof(ReadOnlyText));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _draftDelay?.Cancel();
        _textRequest?.TrySetResult(null);
        _aiContextRequest?.TrySetResult(null);
        _aiApplyRequest?.TrySetResult(false);
        _localization.LanguageChanged -= OnLanguageChanged;
        if (_core is not null) { _core.WebMessageReceived -= OnMessage; _core.NavigationStarting -= OnNavigationStarting; }
        try { _webView?.Close(); } catch { }
    }
}
