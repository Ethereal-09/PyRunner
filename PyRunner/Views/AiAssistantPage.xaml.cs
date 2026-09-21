using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using PyRunner.Models;
using PyRunner.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace PyRunner.Views;

public sealed partial class AiAssistantPage : UserControl, IDisposable
{
    private const string RendererHost = "pyrunner-ai.local";
    private bool _rendererReady;
    private bool _renderQueued;
    private bool _disposed;

    public AiAssistantViewModel ViewModel { get; }
    public CodeEditorViewModel EditorViewModel { get; }

    public AiAssistantPage(AiAssistantViewModel viewModel, CodeEditorViewModel editorViewModel)
    {
        ViewModel = viewModel;
        EditorViewModel = editorViewModel;
        InitializeComponent();
        Loaded += OnLoaded;
        ActualThemeChanged += OnActualThemeChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.MessagesChanged += OnMessagesChanged;
        ViewModel.PromptFocusRequested += OnPromptFocusRequested;
        EditorViewModel.PropertyChanged += OnEditorPropertyChanged;
        UpdatePrimaryAction();
        UpdateMessageSurface();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await InitializeRendererAsync();
        await ViewModel.RestoreLatestConversationAsync();
        await ViewModel.SynchronizeCurrentFileAsync();
        PromptBox.Focus(FocusState.Programmatic);
    }

    private async Task InitializeRendererAsync()
    {
        try
        {
            await MessageWebView.EnsureCoreWebView2Async();
            if (_disposed) return;
            var core = MessageWebView.CoreWebView2;
            UpdateMessageSurface();
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.SetVirtualHostNameToFolderMapping(RendererHost,
                Path.Combine(AppContext.BaseDirectory, "AI", "wwwroot"),
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += OnRendererNavigationStarting;
            core.NavigationCompleted += OnRendererNavigationCompleted;
            core.WebMessageReceived += OnRendererMessageReceived;
            core.Navigate($"https://{RendererHost}/index.html");
        }
        catch { }
    }

    private void OnRendererNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            !uri.Host.Equals(RendererHost, StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.Equals("/index.html", StringComparison.Ordinal)) args.Cancel = true;
    }

    private void OnRendererNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        _rendererReady = args.IsSuccess;
        UpdateMessageSurface();
        QueueRender();
    }

    private async void OnRendererMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var json = JsonDocument.Parse(args.WebMessageAsJson);
            var root = json.RootElement;
            if (!root.TryGetProperty("type", out var type)) return;
            switch (type.GetString())
            {
                case "copy" when root.TryGetProperty("text", out var text) && text.GetString() is { } value:
                    var package = new DataPackage(); package.SetText(value); Clipboard.SetContent(package); break;
                case "retry": await ViewModel.RetryLastAsync(); break;
                case "view-diff": await ViewModel.ViewDiffCommand.ExecuteAsync(null); break;
                case "apply": await ViewModel.ApplyProposalCommand.ExecuteAsync(null); break;
                case "discard": ViewModel.DiscardProposalCommand.Execute(null); break;
            }
        }
        catch { }
    }

    private void OnMessagesChanged(object? sender, EventArgs e) => QueueRender();
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateMessageSurface();
        QueueRender();
    }
    private void OnPromptFocusRequested(object? sender, EventArgs e) => PromptBox.Focus(FocusState.Programmatic);

    private async void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CodeEditorViewModel.FilePath))
            await ViewModel.SynchronizeCurrentFileAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AiAssistantViewModel.IsBusy) or nameof(AiAssistantViewModel.CanSend))
            UpdatePrimaryAction();
        if (e.PropertyName == nameof(AiAssistantViewModel.IsEmpty)) UpdateMessageSurface();
    }

    private void UpdatePrimaryAction()
    {
        if (PrimaryActionButton is null) return;
        var available = ViewModel.IsBusy || ViewModel.CanSend;
        PrimaryActionButton.IsEnabled = true;
        PrimaryActionButton.IsHitTestVisible = available;
        PrimaryActionButton.IsTabStop = available;
        PrimaryActionButton.Opacity = available ? 1 : 0.38;
        PrimaryActionIcon.Glyph = ViewModel.IsBusy ? "\uE71A" : "\uE724";
        AutomationProperties.SetName(PrimaryActionButton, ViewModel.IsBusy ? ViewModel.StopText : ViewModel.SendText);
    }

    private void UpdateMessageSurface()
    {
        if (MessageWebView is null) return;
        if (PageRoot?.Background is SolidColorBrush surface)
            MessageWebView.DefaultBackgroundColor = Windows.UI.Color.FromArgb(
                255, surface.Color.R, surface.Color.G, surface.Color.B);
        if (_rendererReady)
            MessageWebView.Visibility = ViewModel.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void QueueRender()
    {
        if (!_rendererReady || _renderQueued || _disposed) return;
        _renderQueued = true;
        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(32);
            _renderQueued = false;
            RenderMessages();
        });
    }

    private void RenderMessages()
    {
        if (!_rendererReady || _disposed) return;
        var payload = new
        {
            type = "render",
            theme = ActualTheme == ElementTheme.Light ? "light" : "dark",
            labels = new
            {
                copy = ViewModel.CopyText,
                retry = ViewModel.RetryText,
                codeFile = ViewModel.CodeFileFallback,
                viewDiff = ViewModel.ViewDiffText,
                apply = ViewModel.ApplyText,
                discard = ViewModel.DiscardText,
            },
            surface = PageRoot.Background is SolidColorBrush surface
                ? $"#{surface.Color.R:X2}{surface.Color.G:X2}{surface.Color.B:X2}"
                : "transparent",
            messages = ViewModel.Messages.Select(message => new
            {
                id = message.Id,
                role = message.Role.ToString().ToLowerInvariant(),
                kind = message.Kind.ToString().ToLowerInvariant(),
                content = message.Content,
                time = message.CreatedAtUtc.ToLocalTime().ToString("t"),
            }).ToArray(),
            proposal = ViewModel.HasProposal ? new
            {
                title = ViewModel.ProposalTitle,
                summary = ViewModel.ProposalSummary,
                added = ViewModel.ProposalAddedLines,
                deleted = ViewModel.ProposalDeletedLines,
            } : null,
        };
        try { MessageWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); } catch { }
    }

    private async void OnPrimaryActionClick(object sender, RoutedEventArgs e) =>
        await ViewModel.ExecutePrimaryActionAsync();

    private async void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && ViewModel.IsBusy)
        {
            e.Handled = true; await ViewModel.ExecutePrimaryActionAsync(); return;
        }
        if (e.Key != VirtualKey.Enter) return;
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift) return;
        e.Handled = true;
        if (ViewModel.CanSend) await ViewModel.ExecutePrimaryActionAsync();
    }

    private async void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || !ViewModel.IsBusy) return;
        e.Handled = true;
        await ViewModel.ExecutePrimaryActionAsync();
    }

    private void OnQuickPromptClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key }) ViewModel.QuickPromptCommand.Execute(key);
    }

    private async void OnAddContextClick(object sender, RoutedEventArgs e)
    {
        if (App.Current is not App app || app.CurrentWindow is not { } window) return;
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".py");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        try
        {
            var files = await picker.PickMultipleFilesAsync();
            await ViewModel.AddContextFilesAsync(files.Select(file => file.Path));
        }
        catch { }
    }

    private void OnRemoveContextButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) AutomationProperties.SetName(button, ViewModel.RemoveContextText);
    }

    private void OnRemoveContextClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AiContextFileViewModel file })
            ViewModel.RemoveContextCommand.Execute(file);
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 760;
        HeaderActions.Orientation = narrow ? Orientation.Vertical : Orientation.Horizontal;
        ModeSelector.MinWidth = narrow ? 110 : 142;
        ModeSelector.Width = narrow ? 110 : double.NaN;
        ModelSelector.MinWidth = narrow ? 90 : 120;
        ModelSelector.Width = narrow ? 90 : double.NaN;
        ComposerControls.RowDefinitions[1].Height = narrow ? GridLength.Auto : new GridLength(0);
        Grid.SetRow(ComposerStatus, narrow ? 1 : 0);
        Grid.SetColumn(ComposerStatus, narrow ? 0 : 3);
        Grid.SetColumnSpan(ComposerStatus, narrow ? 5 : 1);
        HistoryPanel.Width = Math.Min(360, Math.Max(280, e.NewSize.Width - 24));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ActualThemeChanged -= OnActualThemeChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.MessagesChanged -= OnMessagesChanged;
        ViewModel.PromptFocusRequested -= OnPromptFocusRequested;
        EditorViewModel.PropertyChanged -= OnEditorPropertyChanged;
        if (MessageWebView.CoreWebView2 is { } core)
        {
            core.NavigationStarting -= OnRendererNavigationStarting;
            core.NavigationCompleted -= OnRendererNavigationCompleted;
            core.WebMessageReceived -= OnRendererMessageReceived;
        }
        try { MessageWebView.Close(); } catch { }
    }
}
