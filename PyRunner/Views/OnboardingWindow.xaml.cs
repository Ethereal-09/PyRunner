using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PyRunner.Helpers;
using PyRunner.Models;
using PyRunner.Services;

namespace PyRunner.Views;

/// <summary>
/// 独立首次启动窗口。整个引导期间不依赖、也不创建 MainWindow；目录和解释器只保存在
/// 内存草稿中，最终页确认后交由 App 启动协调器持久化并切换主窗口。
/// </summary>
public sealed partial class OnboardingWindow : Window
{
    private enum WizardStep
    {
        Welcome,
        Paths,
        Interpreter,
        Complete,
    }

    private readonly IScriptPathService _scriptPathService;
    private readonly IInterpreterService _interpreterService;
    private readonly ILocalizationService _localization;
    private readonly ObservableCollection<OnboardingPathItem> _paths = new();
    private readonly ObservableCollection<OnboardingInterpreterItem> _interpreters = new();

    private WizardStep _step;
    private bool _scanStarted;
    private bool _allowClose;
    private bool _exitPromptOpen;
    private bool _transitioning;
    private bool _sizeApplied;
    private bool _pathOperationActive;

    public event EventHandler<OnboardingCompletedEventArgs>? Completed;
    public event EventHandler? ExitRequested;

    public OnboardingWindow(
        IScriptPathService scriptPathService,
        IInterpreterService interpreterService,
        ILocalizationService localization)
    {
        _scriptPathService = scriptPathService;
        _interpreterService = interpreterService;
        _localization = localization;

        InitializeComponent();
        Title = _localization["Wizard_Title"];
        PathItemsControl.ItemsSource = _paths;
        InterpreterItemsControl.ItemsSource = _interpreters;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0xF1, 0xF3, 0xF2);
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);

        Activated += OnActivated;
        AppWindow.Closing += OnWindowClosing;

        ApplyTexts();
        LoadExistingDraft();
        ApplyStep(WizardStep.Welcome);
    }

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        AppWindow.Closing -= OnWindowClosing;
        Close();
    }

    public void ShowCompletionError(string message)
    {
        _transitioning = false;
        SetNavigationEnabled(true);
        CompleteErrorText.Text = message;
        CompleteErrorText.Visibility = Visibility.Visible;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_sizeApplied) return;
        _sizeApplied = true;
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private void LoadExistingDraft()
    {
        try
        {
            foreach (var row in _scriptPathService.GetAll().Where(item => item.Enabled))
            {
                var count = _scriptPathService.Scan(row.Path).Count;
                _paths.Add(new OnboardingPathItem
                {
                    Path = row.Path,
                    ScriptCount = count,
                    CountText = string.Format(_localization["Wizard_Paths_Count"], count),
                    RemoveText = _localization["Wizard_Paths_Remove"],
                });
            }
        }
        catch (Exception ex)
        {
            DebugWrite("读取现有脚本目录失败", ex);
        }
    }

    private void ApplyTexts()
    {
        BrandDescriptionText.Text = _localization["Wizard_Brand_Description"];
        FirstUseText.Text = _localization["Wizard_FirstUse"];
        StepLabel1.Text = _localization["Wizard_Step_Welcome"];
        StepLabel2.Text = _localization["Wizard_Step_Paths"];
        StepLabel3.Text = _localization["Wizard_Step_Interpreter"];
        StepLabel4.Text = _localization["Wizard_Step_Complete"];

        WelcomeTitleText.Text = _localization["Wizard_Welcome_Title"];
        WelcomeDescriptionText.Text = _localization["Wizard_Welcome_Description"];
        FeatureTitle1.Text = _localization["Wizard_Feature_Library_Title"];
        FeatureText1.Text = _localization["Wizard_Feature_Library_Text"];
        FeatureTitle2.Text = _localization["Wizard_Feature_Terminal_Title"];
        FeatureText2.Text = _localization["Wizard_Feature_Terminal_Text"];
        FeatureTitle3.Text = _localization["Wizard_Feature_Interpreter_Title"];
        FeatureText3.Text = _localization["Wizard_Feature_Interpreter_Text"];
        FeatureTitle4.Text = _localization["Wizard_Feature_Smooth_Title"];
        FeatureText4.Text = _localization["Wizard_Feature_Smooth_Text"];

        PathsTitleText.Text = _localization["Wizard_Paths_Title"];
        PathsDescriptionText.Text = _localization["Wizard_Paths_Description"];
        PathInputBox.PlaceholderText = _localization["Wizard_Paths_Placeholder"];
        BrowsePathButton.Content = _localization["Button_Browse"];
        AddPathButton.Content = _localization["Wizard_Paths_Add"];
        PathsTipText.Text = _localization["Wizard_Paths_Tip"];
        AutoRefreshTitleText.Text = _localization["Wizard_AutoRefresh_Title"];
        AutoRefreshDescriptionText.Text = _localization["Wizard_AutoRefresh_Text"];

        InterpreterTitleText.Text = _localization["Wizard_Interp_SelectTitle"];
        InterpreterDescriptionText.Text = _localization["Wizard_Interp_Description"];
        InterpreterScanText.Text = _localization["Wizard_Interp_Scanning"];
        ManualInterpreterButton.Content = _localization["Wizard_Interp_Manual"];
        InterpreterTipText.Text = _localization["Wizard_Interp_Tip"];

        CompleteTitleText.Text = _localization["Wizard_Complete_Title"];
        CompleteDescriptionText.Text = _localization["Wizard_Complete_Text"];
        CompletePathsLabel.Text = _localization["Wizard_Step_Paths"];
        CompleteInterpreterLabel.Text = _localization["Wizard_Step_Interpreter"];
        CenterStartButton.Content = _localization["Wizard_StartUsing"];
        ExitButton.Content = _localization["Button_Exit"];
        BackButton.Content = _localization["Wizard_Back"];
    }

    private void ApplyStep(WizardStep step)
    {
        _step = step;
        WelcomePage.Visibility = step == WizardStep.Welcome ? Visibility.Visible : Visibility.Collapsed;
        PathsPage.Visibility = step == WizardStep.Paths ? Visibility.Visible : Visibility.Collapsed;
        InterpreterPage.Visibility = step == WizardStep.Interpreter ? Visibility.Visible : Visibility.Collapsed;
        CompletePage.Visibility = step == WizardStep.Complete ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = step != WizardStep.Welcome;
        SkipButton.Visibility = step == WizardStep.Complete ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Content = step == WizardStep.Welcome
            ? _localization["Wizard_SkipGuide"]
            : _localization["Wizard_SkipStep"];
        NextButton.Content = step == WizardStep.Complete
            ? _localization["Wizard_Start"]
            : _localization["Wizard_Next"];

        UpdateStepIndicator();
        if (step == WizardStep.Interpreter)
            _ = ScanInterpretersOnceAsync();
        if (step == WizardStep.Complete)
            UpdateCompleteSummary();
    }

    private void UpdateStepIndicator()
    {
        var accent = ThemeBrushes.Get("AccentBrush", 0xFF2FA471);
        var accentText = ThemeBrushes.Get("OnboardingAccentTextBrush", 0xFF07110D);
        var border = ThemeBrushes.Get("OnboardingBorderBrush", 0xFF30363A);
        var primary = ThemeBrushes.Get("OnboardingPrimaryTextBrush", 0xFFF1F3F2);
        var secondary = ThemeBrushes.Get("OnboardingSecondaryTextBrush", 0xFF9AA3A0);

        var circles = new[] { StepCircle1, StepCircle2, StepCircle3, StepCircle4 };
        var numbers = new[] { StepNumber1, StepNumber2, StepNumber3, StepNumber4 };
        var labels = new[] { StepLabel1, StepLabel2, StepLabel3, StepLabel4 };
        var lines = new[] { StepLine1, StepLine2, StepLine3 };
        var dots = new[] { FooterDot1, FooterDot2, FooterDot3, FooterDot4 };
        var current = (int)_step;

        for (var index = 0; index < circles.Length; index++)
        {
            var active = index <= current;
            circles[index].Background = active ? accent : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            circles[index].BorderBrush = active ? accent : border;
            numbers[index].Foreground = active ? accentText : secondary;
            labels[index].Foreground = index == current ? primary : secondary;
            labels[index].FontWeight = index == current ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            // 页脚圆点表示整体进度，当前页以及已经走过的页面都保持品牌绿色。
            dots[index].Fill = index <= current ? accent : border;

            if (index < current && index < 3)
                numbers[index].Text = "✓";
            else
                numbers[index].Text = index == 3 ? "✓" : (index + 1).ToString();
        }

        for (var index = 0; index < lines.Length; index++)
            lines[index].Fill = index < current ? accent : border;
    }

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_transitioning) return;
        switch (_step)
        {
            case WizardStep.Welcome:
                ApplyStep(WizardStep.Paths);
                break;
            case WizardStep.Paths:
                if (!await CommitTypedPathIfPresentAsync()) return;
                ApplyStep(WizardStep.Interpreter);
                break;
            case WizardStep.Interpreter:
                ApplyStep(WizardStep.Complete);
                break;
            case WizardStep.Complete:
                CompleteOnboarding();
                break;
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_transitioning || _step == WizardStep.Welcome) return;
        ApplyStep((WizardStep)((int)_step - 1));
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        if (_transitioning) return;
        ApplyStep(_step switch
        {
            WizardStep.Welcome => WizardStep.Complete,
            WizardStep.Paths => WizardStep.Interpreter,
            WizardStep.Interpreter => WizardStep.Complete,
            _ => WizardStep.Complete,
        });
    }

    private async void OnExitClick(object sender, RoutedEventArgs e) => await RequestExitAsync();

    private async void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        args.Cancel = true;
        await RequestExitAsync();
    }

    private async Task RequestExitAsync()
    {
        if (_exitPromptOpen || _transitioning) return;
        _exitPromptOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = _localization["Wizard_Exit_Title"],
                Content = _localization["Wizard_Exit_Text"],
                PrimaryButtonText = _localization["Wizard_ContinueSetup"],
                SecondaryButtonText = _localization["Wizard_ExitProgram"],
                DefaultButton = ContentDialogButton.Primary,
            };
            DialogHostHelper.Prepare(dialog, RootLayout.XamlRoot, ElementTheme.Dark);
            if (await dialog.ShowAsync() == ContentDialogResult.Secondary)
            {
                _transitioning = true;
                SetNavigationEnabled(false);
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _exitPromptOpen = false;
        }
    }

    private async void OnAddPathClick(object sender, RoutedEventArgs e) => await AddPathAsync(PathInputBox.Text);

    private void OnPathInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_pathOperationActive)
            PathErrorText.Visibility = Visibility.Collapsed;
    }

    private async void OnPathInputKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || _pathOperationActive) return;
        e.Handled = true;
        await AddPathAsync(PathInputBox.Text);
    }

    private async void OnBrowsePathClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
                await AddPathAsync(folder.Path);
        }
        catch (Exception ex)
        {
            ShowPathError(_localization["Wizard_PathAccessError"]);
            DebugWrite("选择脚本目录失败", ex);
        }
    }

    private async Task<bool> CommitTypedPathIfPresentAsync()
    {
        if (string.IsNullOrWhiteSpace(PathInputBox.Text)) return true;
        return await AddPathAsync(PathInputBox.Text);
    }

    private async Task<bool> AddPathAsync(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            ShowPathError(_localization["Wizard_PathInvalid"]);
            PathInputBox.Focus(FocusState.Programmatic);
            return false;
        }
        if (_pathOperationActive) return false;
        _pathOperationActive = true;
        SetPathControlsEnabled(false);
        SetNavigationEnabled(false);
        PathErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var fullPath = Path.GetFullPath(raw.Trim().Trim('"'));
            if (!Directory.Exists(fullPath))
            {
                ShowPathError(_localization["Wizard_PathInvalid"]);
                return false;
            }

            try
            {
                using var probe = Directory.EnumerateFileSystemEntries(fullPath).GetEnumerator();
                _ = probe.MoveNext();
            }
            catch (UnauthorizedAccessException)
            {
                ShowPathError(_localization["Wizard_PathAccessError"]);
                return false;
            }

            if (_paths.Any(item => string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                ShowPathError(_localization["Wizard_PathDuplicate"]);
                return false;
            }

            var count = await Task.Run(() => _scriptPathService.Scan(fullPath).Count);
            _paths.Add(new OnboardingPathItem
            {
                Path = fullPath,
                ScriptCount = count,
                CountText = string.Format(_localization["Wizard_Paths_Count"], count),
                RemoveText = _localization["Wizard_Paths_Remove"],
            });
            PathInputBox.Text = string.Empty;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            ShowPathError(_localization["Wizard_PathAccessError"]);
            return false;
        }
        catch (Exception ex)
        {
            ShowPathError(_localization["Wizard_PathInvalid"]);
            DebugWrite("扫描脚本目录失败", ex);
            return false;
        }
        finally
        {
            _pathOperationActive = false;
            SetPathControlsEnabled(true);
            if (!_transitioning)
                SetNavigationEnabled(true);
        }
    }

    private void SetPathControlsEnabled(bool enabled)
    {
        PathInputBox.IsEnabled = enabled;
        BrowsePathButton.IsEnabled = enabled;
        AddPathButton.IsEnabled = enabled;
    }

    private void OnRemovePathClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OnboardingPathItem item)
            _paths.Remove(item);
    }

    private void ShowPathError(string message)
    {
        PathErrorText.Text = message;
        PathErrorText.Visibility = Visibility.Visible;
    }

    private async Task ScanInterpretersOnceAsync()
    {
        if (_scanStarted) return;
        _scanStarted = true;
        InterpreterScanRing.IsActive = true;
        InterpreterScanningPanel.Visibility = Visibility.Visible;
        InterpreterErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var rows = await Task.Run(() =>
            {
                var result = new List<(string Path, string Version, bool IsDefault)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var existing in _interpreterService.GetAll())
                {
                    if (seen.Add(existing.ExecutablePath))
                        result.Add((existing.ExecutablePath, existing.Version ?? string.Empty, existing.IsDefault));
                }
                foreach (var candidate in _interpreterService.ScanCandidates())
                {
                    if (seen.Add(candidate))
                        result.Add((candidate, _interpreterService.DetectVersion(candidate), false));
                }
                return result;
            });

            _interpreters.Clear();
            var recommendedIndex = rows.FindIndex(row => row.IsDefault);
            if (recommendedIndex < 0 && rows.Count > 0) recommendedIndex = 0;
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                var type = GetInterpreterType(row.Path);
                _interpreters.Add(new OnboardingInterpreterItem
                {
                    ExecutablePath = row.Path,
                    VersionText = row.Version.Length == 0 ? "Python" : $"Python {row.Version}",
                    TypeText = $"·  {type}",
                    BadgeText = index == recommendedIndex ? _localization["Wizard_Interp_Recommended"] : type,
                    IsRecommended = index == recommendedIndex,
                    IsSelected = index == recommendedIndex,
                });
            }

            if (_interpreters.Count == 0)
            {
                InterpreterErrorText.Text = _localization["Wizard_Interp_Empty"];
                InterpreterErrorText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            InterpreterErrorText.Text = _localization["Wizard_Interp_Empty"];
            InterpreterErrorText.Visibility = Visibility.Visible;
            DebugWrite("扫描解释器失败", ex);
        }
        finally
        {
            InterpreterScanRing.IsActive = false;
            InterpreterScanningPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnManualInterpreterClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".exe");
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            if (!Path.GetFileName(file.Path).Equals("python.exe", StringComparison.OrdinalIgnoreCase))
            {
                InterpreterErrorText.Text = _localization["Wizard_Interp_Invalid"];
                InterpreterErrorText.Visibility = Visibility.Visible;
                return;
            }

            var version = await Task.Run(() => _interpreterService.DetectVersion(file.Path));
            if (version.Length == 0)
            {
                InterpreterErrorText.Text = _localization["Wizard_Interp_Invalid"];
                InterpreterErrorText.Visibility = Visibility.Visible;
                return;
            }

            var existing = _interpreters.FirstOrDefault(item =>
                string.Equals(item.ExecutablePath, file.Path, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                existing = new OnboardingInterpreterItem
                {
                    ExecutablePath = file.Path,
                    VersionText = $"Python {version}",
                    TypeText = $"·  {GetInterpreterType(file.Path)}",
                    BadgeText = _localization["Wizard_Interp_ManualBadge"],
                };
                _interpreters.Add(existing);
            }
            SelectInterpreter(existing);
            InterpreterErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            InterpreterErrorText.Text = _localization["Wizard_Interp_Invalid"];
            InterpreterErrorText.Visibility = Visibility.Visible;
            DebugWrite("手动选择解释器失败", ex);
        }
    }

    private void OnInterpreterChoiceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OnboardingInterpreterItem item)
            SelectInterpreter(item);
    }

    private void OnInterpreterRadioLoaded(object sender, RoutedEventArgs e) =>
        UpdateInterpreterRadioVisual(sender as RadioButton);

    private void OnInterpreterRadioCheckedChanged(object sender, RoutedEventArgs e) =>
        UpdateInterpreterRadioVisual(sender as RadioButton);

    private static void UpdateInterpreterRadioVisual(RadioButton? radio)
    {
        if (radio == null) return;
        var selected = radio.IsChecked == true;
        radio.BorderBrush = ThemeBrushes.Get(
            selected ? "AccentBrush" : "OnboardingBorderBrush",
            selected ? 0xFF35A875 : 0xFF30363A);
        radio.Background = ThemeBrushes.Get(
            selected ? "OnboardingAccentSurfaceBrush" : "OnboardingCardBrush",
            selected ? 0x3323684E : 0xFF252A2D);
    }

    private void SelectInterpreter(OnboardingInterpreterItem selected)
    {
        foreach (var item in _interpreters)
            item.IsSelected = ReferenceEquals(item, selected);
    }

    private string GetInterpreterType(string path)
    {
        var normalized = path.Replace('/', '\\');
        return normalized.Contains("\\.venv\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\venv\\", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\\env\\", StringComparison.OrdinalIgnoreCase)
            ? _localization["Wizard_Interp_Venv"]
            : _localization["Wizard_Interp_Global"];
    }

    private void UpdateCompleteSummary()
    {
        var hasPaths = _paths.Count > 0;
        var selectedInterpreter = _interpreters.FirstOrDefault(item => item.IsSelected);
        CompletePathsValue.Text = !hasPaths
            ? _localization["Wizard_NotConfigured"]
            : _paths.Count == 1
                ? _paths[0].Path
                : string.Format(_localization["Wizard_Paths_Summary"], _paths[0].Path, _paths.Count - 1);
        CompleteInterpreterValue.Text = selectedInterpreter?.VersionText ?? _localization["Wizard_NotConfigured"];

        var accent = ThemeBrushes.Get("AccentLightBrush", 0xFF67CEA0);
        var secondary = ThemeBrushes.Get("OnboardingSecondaryTextBrush", 0xFF9AA3A0);
        CompletePathsStatusText.Text = hasPaths ? "✓" : "—";
        CompletePathsStatusText.Foreground = hasPaths ? accent : secondary;
        CompleteInterpreterStatusText.Text = selectedInterpreter != null ? "✓" : "—";
        CompleteInterpreterStatusText.Foreground = selectedInterpreter != null ? accent : secondary;

        ToolTipService.SetToolTip(CompletePathsValue,
            hasPaths ? string.Join(Environment.NewLine, _paths.Select(item => item.Path)) : null);
        ToolTipService.SetToolTip(CompleteInterpreterValue, selectedInterpreter?.ExecutablePath);
    }

    private void OnCompleteClick(object sender, RoutedEventArgs e) => CompleteOnboarding();

    private void CompleteOnboarding()
    {
        if (_transitioning) return;
        _transitioning = true;
        CompleteErrorText.Visibility = Visibility.Collapsed;
        SetNavigationEnabled(false);
        Completed?.Invoke(this, new OnboardingCompletedEventArgs(new OnboardingDraft
        {
            ScriptDirectories = _paths.Select(item => item.Path).ToArray(),
            InterpreterPath = _interpreters.FirstOrDefault(item => item.IsSelected)?.ExecutablePath,
            AutoRefreshScripts = AutoRefreshToggle.IsOn,
        }));
    }

    private void SetNavigationEnabled(bool enabled)
    {
        ExitButton.IsEnabled = enabled;
        BackButton.IsEnabled = enabled && _step != WizardStep.Welcome;
        NextButton.IsEnabled = enabled;
        SkipButton.IsEnabled = enabled;
        CenterStartButton.IsEnabled = enabled;
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugWrite(string context, Exception ex)
    {
#if DEBUG
        DebugLog.WriteLine($"OnboardingWindow: {context}（{ex.Message}）");
#endif
    }
}
