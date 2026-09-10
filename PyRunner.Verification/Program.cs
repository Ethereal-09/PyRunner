using PyRunner.Services;
using PyRunner.Data;
using PyRunner.Models;
using PyRunner.Terminal;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

var cases = new[]
{
    new
    {
        Name = "paths with spaces and no arguments",
        Actual = PythonCommandLine.Build(
            @"C:\Program Files\Python\python.exe",
            @"D:\My Scripts\hello.py",
            null),
        Expected = "\"C:\\Program Files\\Python\\python.exe\" -u \"D:\\My Scripts\\hello.py\"",
    },
    new
    {
        Name = "script arguments follow script path",
        Actual = PythonCommandLine.Build(
            @"C:\Python\python.exe",
            @"D:\scripts\hello.py",
            "  --name \"Ada Lovelace\" --count 2  "),
        Expected = "\"C:\\Python\\python.exe\" -u \"D:\\scripts\\hello.py\" --name \"Ada Lovelace\" --count 2",
    },
};

var failures = 0;
void Verify(bool condition, string name, string? details = null)
{
    if (condition)
    {
        Console.WriteLine($"PASS: {name}");
        return;
    }

    failures++;
    Console.Error.WriteLine($"FAIL: {name}{(string.IsNullOrWhiteSpace(details) ? string.Empty : $" ({details})")}");
}

foreach (var test in cases)
{
    if (test.Actual == test.Expected)
    {
        Console.WriteLine($"PASS: {test.Name}");
        continue;
    }

    failures++;
    Console.Error.WriteLine($"FAIL: {test.Name}");
    Console.Error.WriteLine($"  expected: {test.Expected}");
    Console.Error.WriteLine($"  actual:   {test.Actual}");
}

var sourceRoot = Path.Combine(FindSolutionRoot(), "PyRunner");
var sidebarSource = File.ReadAllText(Path.Combine(sourceRoot, "Views", "SidebarView.xaml"));
var sidebarCode = File.ReadAllText(Path.Combine(sourceRoot, "Views", "SidebarView.xaml.cs"));
var mainWindowSource = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml"));
var mainWindowCode = File.ReadAllText(Path.Combine(sourceRoot, "MainWindow.xaml.cs"));
Verify(
    !sidebarSource.Contains("SidebarAddButton", StringComparison.Ordinal) &&
    !mainWindowSource.Contains("ShortcutAdd", StringComparison.Ordinal) &&
    !mainWindowCode.Contains("OnSidebarAdd", StringComparison.Ordinal) &&
    sidebarSource.Contains("ActionRequested=\"OnOpenSettingsRequested\"", StringComparison.Ordinal) &&
    sidebarCode.Contains("EmptyDirectorySettingsButton", StringComparison.Ordinal),
    "manual script add UI and shortcut are removed");

var scriptListCode = File.ReadAllText(Path.Combine(sourceRoot, "ViewModels", "ScriptListViewModel.cs"));
Verify(
    mainWindowCode.Contains("_scriptListViewModel.SelectByScriptId(vm.ScriptId)", StringComparison.Ordinal) &&
    scriptListCode.Contains("public bool SelectByScriptId(int scriptId)", StringComparison.Ordinal),
    "terminal tab selection synchronizes the active script context");

var dialogHostCode = File.ReadAllText(Path.Combine(sourceRoot, "Helpers", "DialogHostHelper.cs"));
var settingsDialogSource = File.ReadAllText(Path.Combine(sourceRoot, "Views", "SettingsDialog.xaml"));
var scriptEditDialogSource = File.ReadAllText(Path.Combine(sourceRoot, "Views", "ScriptEditDialog.xaml"));
var appCode = File.ReadAllText(Path.Combine(sourceRoot, "App.xaml.cs"));
var onboardingSource = File.ReadAllText(Path.Combine(sourceRoot, "Views", "OnboardingWindow.xaml"));
var onboardingCode = File.ReadAllText(Path.Combine(sourceRoot, "Views", "OnboardingWindow.xaml.cs"));
var onboardingDraftCode = File.ReadAllText(Path.Combine(sourceRoot, "Models", "OnboardingDraft.cs"));
Verify(
    mainWindowSource.Contains("x:Name=\"TitleBarDragRegion\"", StringComparison.Ordinal) &&
    mainWindowCode.Contains("SetTitleBar(TitleBarDragRegion)", StringComparison.Ordinal) &&
    !mainWindowCode.Contains("SetTitleBar(TitleBarArea)", StringComparison.Ordinal) &&
    mainWindowCode.Contains("PrepareDialog(ContentDialog dialog)", StringComparison.Ordinal) &&
    dialogHostCode.Contains("dialog.RequestedTheme", StringComparison.Ordinal) &&
    settingsDialogSource.Contains("Background=\"{ThemeResource PanelSurfaceRaisedBrush}\"", StringComparison.Ordinal) &&
    scriptEditDialogSource.Contains("Background=\"{ThemeResource PanelSurfaceRaisedBrush}\"", StringComparison.Ordinal) &&
    !settingsDialogSource.Contains("TintColor=\"#282A2F\"", StringComparison.Ordinal),
    "title bar pointer hit testing and dialog theme propagation are wired");

Verify(
    onboardingSource.Contains("x:Class=\"PyRunner.Views.OnboardingWindow\"", StringComparison.Ordinal) &&
    onboardingSource.Contains("AutomationProperties.AutomationId=\"OnboardingWindow\"", StringComparison.Ordinal) &&
    onboardingSource.Contains("OnboardingNextButton", StringComparison.Ordinal) &&
    onboardingCode.Contains("public sealed partial class OnboardingWindow : Window", StringComparison.Ordinal) &&
    onboardingCode.Contains("AppWindow.Closing += OnWindowClosing", StringComparison.Ordinal) &&
    onboardingCode.Contains("Wizard_Exit_Title", StringComparison.Ordinal) &&
    onboardingDraftCode.Contains("AutoRefreshScripts", StringComparison.Ordinal),
    "independent four-step onboarding window, draft, navigation, and close confirmation are wired");

Verify(
    appCode.Contains("LaunchInitialWindow()", StringComparison.Ordinal) &&
    appCode.Contains("settings.FirstRunCompleted && settings.FirstRunVersion >= CurrentOnboardingVersion", StringComparison.Ordinal) &&
    appCode.Contains("CreateAndActivateOnboardingWindow()", StringComparison.Ordinal) &&
    appCode.Contains("current.FirstRunCompleted = true", StringComparison.Ordinal) &&
    appCode.IndexOf("CreateAndActivateMainWindow();", appCode.IndexOf("OnOnboardingCompleted", StringComparison.Ordinal), StringComparison.Ordinal) <
        appCode.IndexOf("current.FirstRunCompleted = true", appCode.IndexOf("OnOnboardingCompleted", StringComparison.Ordinal), StringComparison.Ordinal) &&
    !mainWindowCode.Contains("FirstRunWizard", StringComparison.Ordinal) &&
    !appCode.Contains("FirstRunWizard", StringComparison.Ordinal),
    "main window is gated until onboarding completion and completion is marked only afterward");

var projectSource = File.ReadAllText(Path.Combine(sourceRoot, "PyRunner.csproj"));
var installerSource = File.ReadAllText(Path.Combine(FindSolutionRoot(), "Installer", "PyRunner.iss"));
var installerBuildSource = File.ReadAllText(Path.Combine(FindSolutionRoot(), "Installer", "build-installer.ps1"));
Verify(
    projectSource.Contains("<ApplicationIcon>Assets\\AppIcon.ico</ApplicationIcon>", StringComparison.Ordinal) &&
    projectSource.Contains("<Content Include=\"Assets\\AppIcon.ico\">", StringComparison.Ordinal) &&
    mainWindowCode.Contains("AppWindow.SetIcon(appIconPath)", StringComparison.Ordinal) &&
    installerSource.Contains("SetupIconFile=..\\PyRunner\\Assets\\AppIcon.ico", StringComparison.Ordinal),
    "installer, taskbar, and desktop shortcuts share the PyRunner application icon");

Verify(
    !installerSource.Contains("#define AppVersion \"", StringComparison.Ordinal) &&
    installerSource.Contains("#ifndef AppVersion", StringComparison.Ordinal) &&
    installerBuildSource.Contains("$projectXml.Project.PropertyGroup.Version", StringComparison.Ordinal) &&
    installerBuildSource.Contains("/DAppVersion=$appVersion", StringComparison.Ordinal) &&
    installerBuildSource.Contains("PyRunner-Setup-$appVersion-x64.exe", StringComparison.Ordinal) &&
    projectSource.Contains("CopyOpenSourceNoticesToPublish", StringComparison.Ordinal),
    "installer consumes the project version and published license notices without a hard-coded fallback");

var terminalSource = File.ReadAllText(Path.Combine(sourceRoot, "Terminal", "wwwroot", "terminal.js"));
var terminalVmCode = File.ReadAllText(Path.Combine(sourceRoot, "ViewModels", "TerminalSessionViewModel.cs"));
Verify(
    terminalSource.Contains("attachCustomKeyEventHandler", StringComparison.Ordinal) &&
    terminalSource.Contains("addEventListener('keydown'", StringComparison.Ordinal) &&
    terminalSource.Contains("addEventListener('paste'", StringComparison.Ordinal) &&
    terminalSource.Contains("stopImmediatePropagation()", StringComparison.Ordinal) &&
    terminalSource.Contains("addEventListener('contextmenu'", StringComparison.Ordinal) &&
    terminalSource.Contains("copyRequest", StringComparison.Ordinal) &&
    terminalSource.Contains("pasteRequest", StringComparison.Ordinal) &&
    !mainWindowSource.Contains("Key=\"V\" Modifiers=\"Control,Shift\"", StringComparison.Ordinal) &&
    !mainWindowSource.Contains("Key=\"C\" Modifiers=\"Control,Shift\"", StringComparison.Ordinal) &&
    terminalVmCode.Contains("PasteFromClipboardAsync", StringComparison.Ordinal),
    "terminal clipboard shortcuts are captured once and right-click paste is wired");

var terminalAssetsRoot = Path.Combine(sourceRoot, "Terminal", "wwwroot");
var terminalHtml = File.ReadAllText(Path.Combine(terminalAssetsRoot, "index.html"));
var vendorManifestPath = Path.Combine(terminalAssetsRoot, "vendor-manifest.json");
using (var vendorManifest = JsonDocument.Parse(File.ReadAllText(vendorManifestPath)))
{
    var packages = vendorManifest.RootElement.GetProperty("packages");
    var declaredFiles = packages.EnumerateArray()
        .SelectMany(package => package.GetProperty("files").EnumerateArray())
        .ToDictionary(
            file => file.GetProperty("path").GetString()!,
            file => file.GetProperty("sha256").GetString()!,
            StringComparer.Ordinal);
    var expectedVersions = packages.EnumerateArray().ToDictionary(
        package => package.GetProperty("name").GetString()!,
        package => package.GetProperty("version").GetString()!,
        StringComparer.Ordinal);
    var allAssetHashesMatch = declaredFiles.All(item =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(terminalAssetsRoot, item.Key))))
            .Equals(item.Value, StringComparison.OrdinalIgnoreCase));

    Verify(
        vendorManifest.RootElement.GetProperty("schemaVersion").GetInt32() == 1 &&
        expectedVersions.GetValueOrDefault("@xterm/xterm") == "5.5.0" &&
        expectedVersions.GetValueOrDefault("@xterm/addon-fit") == "0.10.0" &&
        declaredFiles.Count == 3 &&
        allAssetHashesMatch,
        "vendored xterm package versions and SHA-256 hashes match the provenance manifest");
}

Verify(
    terminalHtml.Contains("<script src=\"xterm.js\"></script>", StringComparison.Ordinal) &&
    terminalHtml.Contains("<script src=\"xterm-addon-fit.js\"></script>", StringComparison.Ordinal) &&
    terminalHtml.Contains("<script src=\"terminal.js\"></script>", StringComparison.Ordinal) &&
    terminalSource.Contains("const fitAddon = new FitAddon.FitAddon();", StringComparison.Ordinal) &&
    terminalSource.Contains("term.loadAddon(fitAddon);", StringComparison.Ordinal) &&
    terminalSource.Contains("fitAddon.fit();", StringComparison.Ordinal) &&
    terminalSource.Contains("window.addEventListener('resize'", StringComparison.Ordinal) &&
    terminalSource.Contains("sendToHost({ type: 'resize', cols, rows });", StringComparison.Ordinal),
    "xterm and FitAddon keep their existing initialization and resize wiring");

var darkThemeSource = File.ReadAllText(Path.Combine(sourceRoot, "Assets", "Themes", "DarkTheme.xaml"));
var lightThemeSource = File.ReadAllText(Path.Combine(sourceRoot, "Assets", "Themes", "LightTheme.xaml"));
var darkThemeKeys = Regex.Matches(darkThemeSource, "x:Key=\"([^\"]+)\"")
    .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
var lightThemeKeys = Regex.Matches(lightThemeSource, "x:Key=\"([^\"]+)\"")
    .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
var appXamlSource = File.ReadAllText(Path.Combine(sourceRoot, "App.xaml"));
var settingsSource = File.ReadAllText(Path.Combine(sourceRoot, "Models", "AppSettings.cs"));
Verify(
    darkThemeKeys.Count > 0 && darkThemeKeys.SetEquals(lightThemeKeys) &&
    appXamlSource.Contains("ThemeResources.xaml", StringComparison.Ordinal) &&
    mainWindowSource.Contains("TitleBarThemeButton", StringComparison.Ordinal) &&
    !mainWindowSource.Contains("TitleBarLanguageButton", StringComparison.Ordinal) &&
    settingsSource.Contains("public string Theme { get; set; } = \"Dark\";", StringComparison.Ordinal) &&
    terminalSource.Contains("term.options.theme = terminalThemes[themeName]", StringComparison.Ordinal),
    "light and dark themes have matching resources and runtime switching",
    $"dark={darkThemeKeys.Count}, light={lightThemeKeys.Count}");

Verify(
    appXamlSource.Contains("<VisualState x:Name=\"PointerFocused\" />", StringComparison.Ordinal) &&
    mainWindowSource.Contains("x:Name=\"ThemeButton\"", StringComparison.Ordinal) &&
    mainWindowSource.Contains("BorderBrush=\"{ThemeResource SecondaryButtonBorderBrush}\"", StringComparison.Ordinal),
    "mouse clicks do not leave persistent button focus chrome");

var helpPageSource = File.ReadAllText(Path.Combine(sourceRoot, "Views", "HelpAboutPage.xaml"));
var helpPageCode = File.ReadAllText(Path.Combine(sourceRoot, "Views", "HelpAboutPage.xaml.cs"));
var helpViewModelCode = File.ReadAllText(Path.Combine(sourceRoot, "ViewModels", "HelpAboutViewModel.cs"));
var metadataCode = File.ReadAllText(Path.Combine(sourceRoot, "Services", "AppMetadataService.cs"));
var clipboardCode = File.ReadAllText(Path.Combine(sourceRoot, "Services", "ClipboardService.cs"));
var updateCode = File.ReadAllText(Path.Combine(sourceRoot, "Services", "GitHubUpdateService.cs"));
var updateModelsCode = File.ReadAllText(Path.Combine(sourceRoot, "Models", "UpdateCheckModels.cs"));
var updateCoordinatorCode = File.ReadAllText(Path.Combine(sourceRoot, "Services", "UpdateCheckCoordinator.cs"));
var linkCode = File.ReadAllText(Path.Combine(sourceRoot, "Services", "ProductLinksOptions.cs"));
var navigationCode = File.ReadAllText(Path.Combine(sourceRoot, "Services", "ShellNavigationService.cs"));
Verify(
    mainWindowSource.Contains("x:Name=\"NavHelpButton\"", StringComparison.Ordinal) &&
    mainWindowSource.Contains("x:Name=\"HelpPageHost\"", StringComparison.Ordinal) &&
    mainWindowSource.Contains("<Frame", StringComparison.Ordinal) &&
    !mainWindowSource.Contains("x:Name=\"InfoButton\"", StringComparison.Ordinal) &&
    !mainWindowCode.Contains("OnInfoClick", StringComparison.Ordinal) &&
    mainWindowCode.Contains("_helpPage.Refresh();", StringComparison.Ordinal) &&
    mainWindowCode.Contains("_navigation.Navigate(ShellPage.Help)", StringComparison.Ordinal) &&
    navigationCode.Contains("interface IShellNavigationService", StringComparison.Ordinal),
    "embedded help navigation replaces the duplicate script info button");

Verify(
    helpPageSource.Contains("MaxWidth=\"1050\"", StringComparison.Ordinal) &&
    helpPageSource.Contains("HorizontalScrollMode=\"Disabled\"", StringComparison.Ordinal) &&
    helpPageSource.Contains("SizeChanged=\"OnHelpScrollerSizeChanged\"", StringComparison.Ordinal) &&
    helpPageCode.Contains("ApplyResponsiveLayout(contentWidth < 720)", StringComparison.Ordinal) &&
    helpPageSource.Contains("HelpAddFolderButton", StringComparison.Ordinal) &&
    helpPageSource.Contains("HelpInterpreterButton", StringComparison.Ordinal) &&
    helpPageSource.Contains("HelpCopyInfoButton", StringComparison.Ordinal) &&
    helpPageSource.Contains("Command=\"{Binding CopySoftwareInfoCommand}\"", StringComparison.Ordinal) &&
    helpPageSource.Contains("Command=\"{Binding NavigateToSettingsCommand}\"", StringComparison.Ordinal) &&
    helpPageCode.Contains("public HelpAboutViewModel ViewModel", StringComparison.Ordinal) &&
    !helpPageCode.Contains("Clipboard.SetContent", StringComparison.Ordinal) &&
    !helpPageCode.Contains("_localization", StringComparison.Ordinal),
    "help page is responsive, scroll-safe, and exposes the documented local actions");

Verify(
    metadataCode.Contains("AssemblyMetadataAttribute", StringComparison.Ordinal) &&
    metadataCode.Contains("AssemblyInformationalVersionAttribute", StringComparison.Ordinal) &&
    metadataCode.Contains("RuntimeInformation.FrameworkDescription", StringComparison.Ordinal) &&
    metadataCode.Contains("RuntimeInformation.OSDescription", StringComparison.Ordinal) &&
    metadataCode.Contains("CoreWebView2Environment", StringComparison.Ordinal) &&
    helpViewModelCode.Contains("BuildPrivacySafeSoftwareInfo", StringComparison.Ordinal) &&
    !helpViewModelCode.Contains("ExecutablePath", StringComparison.Ordinal) &&
    !helpViewModelCode.Contains("Environment.GetEnvironmentVariable", StringComparison.Ordinal) &&
    clipboardCode.Contains("interface IClipboardService", StringComparison.Ordinal) &&
    updateModelsCode.Contains("enum UpdateCheckStatus", StringComparison.Ordinal) &&
    updateCode.Contains("UpdateCheckStatus.Timeout", StringComparison.Ordinal) &&
    linkCode.Contains("interface IExternalLinkService", StringComparison.Ordinal),
    "copied diagnostics use build/runtime metadata without private paths or environment variables");

Verify(
    appCode.Contains("https://api.github.com/repos/Ethereal-09/PyRunner/releases/latest", StringComparison.Ordinal) &&
    appCode.Contains("https://github.com/Ethereal-09/PyRunner/releases", StringComparison.Ordinal) &&
    appCode.Contains("application/vnd.github+json", StringComparison.Ordinal) &&
    appCode.Contains("X-GitHub-Api-Version", StringComparison.Ordinal) &&
    updateCoordinatorCode.Contains("TimeSpan.FromHours(24)", StringComparison.Ordinal) &&
    mainWindowCode.Contains("TimeSpan.FromSeconds(7)", StringComparison.Ordinal) &&
    mainWindowCode.Contains("UpdateCheckCoordinator", StringComparison.Ordinal) &&
    !mainWindowCode.Contains("HelpAboutViewModel", StringComparison.Ordinal),
    "fixed GitHub endpoints, required headers, delayed startup check, and shared coordinator are wired");

Verify(
    helpPageSource.Contains("HelpUpdateStatusPanel", StringComparison.Ordinal) &&
    helpPageSource.Contains("DownloadUpdateCommand", StringComparison.Ordinal) &&
    helpPageSource.Contains("OpenExternalLinkCommand", StringComparison.Ordinal) &&
    helpPageSource.Contains("CommandParameter=\"Releases\"", StringComparison.Ordinal),
    "existing help page exposes the incremental update status and release actions");

Verify(
    settingsDialogSource.Contains("x:Name=\"ScriptPathsGroupTitle\"", StringComparison.Ordinal) &&
    mainWindowCode.Contains("focusScriptPaths: section == HelpSettingsSection.ScriptPaths", StringComparison.Ordinal) &&
    mainWindowCode.Contains("focusInterpreters: section == HelpSettingsSection.Interpreters", StringComparison.Ordinal),
    "help quick actions focus the existing settings sections without mutating configuration");

var artifactsRoot = Path.Combine(FindSolutionRoot(), "artifacts");
var artifactPythonFiles = Directory.Exists(artifactsRoot)
    ? Directory.EnumerateFiles(artifactsRoot, "*.py", SearchOption.AllDirectories)
    : Enumerable.Empty<string>();
Verify(
    !artifactPythonFiles.Any(path =>
        path.Contains("ui-demo-scripts", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("gui-test-scripts", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("seed_gui_test.py", StringComparison.OrdinalIgnoreCase)),
    "bundled demo script fixtures are empty");

failures += await HelpAboutViewModelTests.RunAsync();
failures += await GitHubUpdateServiceTests.RunAsync();
failures += await JsonSettingsServiceTests.RunAsync();
failures += await UpdateStateStoreTests.RunAsync();
failures += ProductVersionParserTests.Run();

var temporaryRoot = Path.Combine(Path.GetTempPath(), "PyRunner.Verification", Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(temporaryRoot);

    // PyRunner is a WinUI (GUI) process and has no standard handles. Mirror
    // that host shape so CreateProcess cannot route the child around ConPTY
    // through this verification process's redirected stdout/stderr handles.
    _ = Console.Out;
    _ = Console.Error;
    SetStdHandle(-10, IntPtr.Zero);
    SetStdHandle(-11, IntPtr.Zero);
    SetStdHandle(-12, IntPtr.Zero);

    // ConPTY 全链路：独立探针输出 ANSI/中文，等待 stdin，再以指定退出码结束。
    var probeCandidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "PyRunner.TerminalProbe.exe"),
        Path.Combine(FindSolutionRoot(), "artifacts", "test-probe", "PyRunner.TerminalProbe.exe"),
    };
    var probePath = probeCandidates.FirstOrDefault(File.Exists);
    if (probePath is null)
        throw new FileNotFoundException($"Terminal probe was not built. Checked: {string.Join(", ", probeCandidates)}");

    var outputBytes = new List<byte>();
    var inputPrompt = new ManualResetEventSlim();
    var processExited = new ManualResetEventSlim();
    var probeExitCode = int.MinValue;
    using (var session = new PtySession(
        $"\"{probePath}\"",
        100, 30, action => { action(); return true; }, temporaryRoot))
    {
        session.OutputReceived += base64 =>
        {
            lock (outputBytes)
            {
                outputBytes.AddRange(Convert.FromBase64String(base64));
                if (Encoding.UTF8.GetString(outputBytes.ToArray()).Contains("INPUT>", StringComparison.Ordinal))
                    inputPrompt.Set();
            }
        };
        session.ProcessExited += code =>
        {
            probeExitCode = code;
            processExited.Set();
        };

        session.Start();
        if (!inputPrompt.Wait(TimeSpan.FromSeconds(8)))
            throw new TimeoutException("ConPTY probe did not reach input prompt");
        session.WriteInput("PyRunner-input\r");
        if (!processExited.Wait(TimeSpan.FromSeconds(8)))
            throw new TimeoutException("ConPTY probe did not exit");
    }

    string probeOutput;
    lock (outputBytes) { probeOutput = Encoding.UTF8.GetString(outputBytes.ToArray()); }
    if (probeExitCode != 7 ||
        !probeOutput.Contains("\u001b[32m", StringComparison.Ordinal) ||
        !probeOutput.Contains("READY 中文", StringComparison.Ordinal) ||
        !probeOutput.Contains("\u001b[m", StringComparison.Ordinal) ||
        !probeOutput.Contains("ECHO=PyRunner-input", StringComparison.Ordinal))
    {
        failures++;
        Console.Error.WriteLine("FAIL: ConPTY output, ANSI, Unicode, input, and exit code");
        Console.Error.WriteLine(probeOutput);
    }
    else
    {
        Console.WriteLine("PASS: ConPTY output, ANSI, Unicode, input, and exit code");
    }

    // 使用本机真实 Python 验证产品主链路，而不只验证通用控制台探针。
    var pythonPath = FindPythonExecutable();
    if (pythonPath == null)
    {
        failures++;
        Console.Error.WriteLine("FAIL: no real python.exe found for end-to-end verification");
    }
    else
    {
        Console.WriteLine($"INFO: real Python path = {pythonPath}");
        var interactiveScript = Path.Combine(temporaryRoot, "interactive 验收.py");
        File.WriteAllText(
            interactiveScript,
            "import sys\n"
            + "print('\\x1b[35mPY_READY 中文\\x1b[0m', flush=True)\n"
            + "value = input('PY_INPUT>')\n"
            + "print(f'PY_ECHO={value}', flush=True)\n"
            + "sys.exit(9)\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var pythonOutputBytes = new List<byte>();
        var pythonPrompt = new ManualResetEventSlim();
        var pythonExited = new ManualResetEventSlim();
        var pythonExitCode = int.MinValue;
        using (var session = new PtySession(
            PythonCommandLine.Build(pythonPath, interactiveScript, null),
            100, 30, action => { action(); return true; }, temporaryRoot))
        {
            session.OutputReceived += base64 =>
            {
                lock (pythonOutputBytes)
                {
                    pythonOutputBytes.AddRange(Convert.FromBase64String(base64));
                    if (Encoding.UTF8.GetString(pythonOutputBytes.ToArray()).Contains("PY_INPUT>", StringComparison.Ordinal))
                        pythonPrompt.Set();
                }
            };
            session.ProcessExited += code =>
            {
                pythonExitCode = code;
                pythonExited.Set();
            };

            Console.WriteLine("INFO: starting real Python input() probe");
            session.Start();
            Console.WriteLine("INFO: real Python process started");
            if (!pythonPrompt.Wait(TimeSpan.FromSeconds(8)))
                throw new TimeoutException("Python input() prompt was not received");
            Console.WriteLine("INFO: real Python input() prompt received");
            session.WriteInput("中文-input\r");
            if (!pythonExited.Wait(TimeSpan.FromSeconds(8)))
                throw new TimeoutException("Interactive Python script did not exit");
            Console.WriteLine("INFO: real Python input() probe exited");
        }

        string pythonOutput;
        lock (pythonOutputBytes) { pythonOutput = Encoding.UTF8.GetString(pythonOutputBytes.ToArray()); }
        if (pythonExitCode != 9 ||
            !pythonOutput.Contains("\u001b[35m", StringComparison.Ordinal) ||
            !pythonOutput.Contains("PY_READY 中文", StringComparison.Ordinal) ||
            !pythonOutput.Contains("PY_ECHO=中文-input", StringComparison.Ordinal))
        {
            failures++;
            Console.Error.WriteLine("FAIL: real Python ANSI, Unicode input(), and exit code");
            Console.Error.WriteLine(pythonOutput);
        }
        else
        {
            Console.WriteLine($"PASS: real Python {GetPythonVersion(pythonPath)} ANSI, Unicode input(), and exit code");
        }

        var interruptScript = Path.Combine(temporaryRoot, "interrupt.py");
        File.WriteAllText(
            interruptScript,
            "import sys, time\n"
            + "print('INTERRUPT_READY', flush=True)\n"
            + "try:\n"
            + "    time.sleep(30)\n"
            + "except KeyboardInterrupt:\n"
            + "    print('INTERRUPTED', flush=True)\n"
            + "    sys.exit(130)\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var interruptOutputBytes = new List<byte>();
        var interruptReady = new ManualResetEventSlim();
        var interruptExited = new ManualResetEventSlim();
        var interruptExitCode = int.MinValue;
        using (var session = new PtySession(
            PythonCommandLine.Build(pythonPath, interruptScript, null),
            100, 30, action => { action(); return true; }, temporaryRoot))
        {
            session.OutputReceived += base64 =>
            {
                lock (interruptOutputBytes)
                {
                    interruptOutputBytes.AddRange(Convert.FromBase64String(base64));
                    if (Encoding.UTF8.GetString(interruptOutputBytes.ToArray()).Contains("INTERRUPT_READY", StringComparison.Ordinal))
                        interruptReady.Set();
                }
            };
            session.ProcessExited += code =>
            {
                interruptExitCode = code;
                interruptExited.Set();
            };

            Console.WriteLine("INFO: starting real Python Ctrl+C probe");
            session.Start();
            Console.WriteLine("INFO: real Python Ctrl+C process started");
            if (!interruptReady.Wait(TimeSpan.FromSeconds(8)))
                throw new TimeoutException("Python interrupt probe was not ready");
            Console.WriteLine("INFO: real Python Ctrl+C probe ready");
            session.WriteInput("\u0003");
            if (!interruptExited.Wait(TimeSpan.FromSeconds(8)))
                throw new TimeoutException("Python did not respond to Ctrl+C");
            Console.WriteLine("INFO: real Python Ctrl+C probe exited");
        }

        string interruptOutput;
        lock (interruptOutputBytes) { interruptOutput = Encoding.UTF8.GetString(interruptOutputBytes.ToArray()); }
        if (interruptExitCode != 130 || !interruptOutput.Contains("INTERRUPTED", StringComparison.Ordinal))
        {
            failures++;
            Console.Error.WriteLine($"FAIL: real Python Ctrl+C interrupt (exit={interruptExitCode})");
            Console.Error.WriteLine(interruptOutput);
        }
        else
        {
            Console.WriteLine("PASS: real Python Ctrl+C interrupt");
        }
    }

    // Job Object：关闭会话后，脚本启动的子进程也必须退出。
    var childOutput = new List<byte>();
    var childStarted = new ManualResetEventSlim();
    var childPid = 0;
    using (var session = new PtySession(
        $"\"{probePath}\" --spawn-child",
        100, 30, action => { action(); return true; }, temporaryRoot))
    {
        session.OutputReceived += base64 =>
        {
            lock (childOutput)
            {
                childOutput.AddRange(Convert.FromBase64String(base64));
                var text = Encoding.UTF8.GetString(childOutput.ToArray());
                var match = Regex.Match(text, @"CHILD_PID=(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out childPid))
                    childStarted.Set();
            }
        };
        session.Start();
        if (!childStarted.Wait(TimeSpan.FromSeconds(8)))
            throw new TimeoutException("Job Object probe child did not start");
    }

    Thread.Sleep(500);
    var childAlive = false;
    try
    {
        using var child = System.Diagnostics.Process.GetProcessById(childPid);
        childAlive = !child.HasExited;
    }
    catch (ArgumentException)
    {
        childAlive = false;
    }

    if (childAlive)
    {
        failures++;
        Console.Error.WriteLine($"FAIL: Job Object left child process {childPid} running");
    }
    else
    {
        Console.WriteLine("PASS: Job Object terminates the child process tree");
    }

    var factory = new SqliteConnectionFactory(temporaryRoot);
    var migrator = new SchemaMigrator(factory);
    migrator.Migrate();
    migrator.Migrate();

    using (var connection = factory.CreateOpenConnection())
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaVersion;";
        Verify(Convert.ToInt32(command.ExecuteScalar()) == 1, "schema migration is idempotent");
    }

    var scriptsDirectory = Path.Combine(temporaryRoot, "scripts folder");
    var nestedDirectory = Path.Combine(scriptsDirectory, "nested");
    Directory.CreateDirectory(nestedDirectory);
    var scriptFile = Path.Combine(scriptsDirectory, "alpha script.py");
    var importedFile = Path.Combine(nestedDirectory, "beta.py");
    var dependencyDirectory = Path.Combine(scriptsDirectory, "venv", "Lib", "site-packages", "sample_package");
    var dependencyFile = Path.Combine(dependencyDirectory, "dependency.py");
    var outsideFile = Path.Combine(temporaryRoot, "outside.py");
    Directory.CreateDirectory(dependencyDirectory);
    File.WriteAllText(scriptFile, "print('alpha')", new UTF8Encoding(false));
    File.WriteAllText(importedFile, "print('beta')", new UTF8Encoding(false));
    File.WriteAllText(dependencyFile, "print('dependency')", new UTF8Encoding(false));
    File.WriteAllText(outsideFile, "print('outside')", new UTF8Encoding(false));

    var pathService = new ScriptPathService(factory);
    var storedPath = pathService.Add(scriptsDirectory);
    Verify(pathService.GetAll().Count == 1, "script directory add and list");
    Verify(pathService.Scan(scriptsDirectory).Count == 2 &&
           !pathService.Scan(scriptsDirectory).Contains(dependencyFile, StringComparer.OrdinalIgnoreCase),
        "configured directory scan excludes Python environments");
    var duplicatePathRejected = false;
    try { pathService.Add(scriptsDirectory.ToUpperInvariant()); }
    catch (ValidationException ex) when (ex.LocalizationKey == "Error_PathDuplicate") { duplicatePathRejected = true; }
    Verify(duplicatePathRejected, "case-insensitive duplicate script directory rejection");

    var interpreterService = new InterpreterService(factory);
    var interpreter = interpreterService.AddFromPath(pythonPath!);
    Verify(interpreter.Version?.StartsWith("3.13", StringComparison.Ordinal) == true,
        "interpreter version detection", interpreter.Version);
    Verify(interpreterService.GetDefault()?.Id == interpreter.Id, "first interpreter becomes default");

    var scriptService = new ScriptService(factory);
    var script = new Script
    {
        Name = "Alpha",
        FilePath = scriptFile,
        Category = "Automation",
        Tags = "smoke,python",
        Description = "Business verification target",
        InterpreterId = interpreter.Id,
        Arguments = "--sample 1",
        WorkingDirectory = scriptsDirectory,
    };
    var scriptId = scriptService.Add(script);
    Verify(scriptId > 0 && scriptService.GetById(scriptId)?.Name == "Alpha", "script add and get");
    Verify(scriptService.Search("SMOKE").Any(item => item.Id == scriptId), "script search by tag is case-insensitive");

    scriptService.Add(new Script { Name = "Dependency", FilePath = dependencyFile });
    scriptService.Add(new Script { Name = "Outside", FilePath = outsideFile });

    var duplicateScriptRejected = false;
    try { scriptService.Add(new Script { Name = "Duplicate", FilePath = scriptFile.ToUpperInvariant() }); }
    catch (ValidationException ex) when (ex.LocalizationKey == "Error_PathDuplicate") { duplicateScriptRejected = true; }
    Verify(duplicateScriptRejected, "case-insensitive duplicate script rejection");

    scriptService.ToggleFavorite(scriptId, true);
    script.Name = "Alpha Updated";
    // 模拟编辑表单遗漏非编辑字段的旧缺陷：Update 必须只更新可编辑字段，
    // 不得用传入对象的默认 false 覆盖数据库中的收藏状态。
    script.IsFavorite = false;
    scriptService.Update(script);
    var favoriteAfterEdit = scriptService.GetById(scriptId);
    Verify(favoriteAfterEdit?.Name == "Alpha Updated" && favoriteAfterEdit.IsFavorite,
        "script edit preserves favorite state");

    scriptService.ToggleFavorite(scriptId, false);
    var updatedScript = scriptService.GetById(scriptId);
    Verify(updatedScript?.Name == "Alpha Updated" && updatedScript.IsFavorite == false,
        "script update and favorite toggle");

    var imported = scriptService.ImportFromPaths(new[] { scriptsDirectory });
    var synchronizedScripts = scriptService.GetAll();
    Verify(imported == 3 && synchronizedScripts.Count == 2 &&
           synchronizedScripts.All(item =>
               !string.Equals(item.FilePath, dependencyFile, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(item.FilePath, outsideFile, StringComparison.OrdinalIgnoreCase)),
        "directory sync keeps only selected user scripts");

    var watchedFile = Path.Combine(scriptsDirectory, "created later.py");
    File.WriteAllText(watchedFile, "print('created later')", new UTF8Encoding(false));
    var importedAfterChange = scriptService.ImportFromPaths(new[] { scriptsDirectory });
    Verify(importedAfterChange == 1 &&
           scriptService.GetAll().Any(item =>
               string.Equals(item.FilePath, watchedFile, StringComparison.OrdinalIgnoreCase)),
        "configured directory rescan imports newly created scripts");

    var runRecordService = new RunRecordService(factory);
    for (var index = 0; index < 12; index++)
    {
        var recordId = runRecordService.StartRun(scriptId);
        var output = index == 11 ? new string('x', 210 * 1024) + "TAIL" : $"run-{index}";
        runRecordService.FinishRun(recordId, index == 11 ? 5 : 0,
            index == 11 ? RunStatus.Failed : RunStatus.Success, output);
    }

    var recent = runRecordService.GetRecent(scriptId, 20);
    Verify(recent.Count == 10, "run history retention keeps the newest 10 records", recent.Count.ToString());
    Verify(recent[0].RunStatus == RunStatus.Failed && recent[0].Output?.Length == 200 * 1024 &&
           recent[0].Output?.EndsWith("TAIL", StringComparison.Ordinal) == true,
        "run history status and 200 KB tail truncation");

    scriptService.Delete(scriptId);
    Verify(File.Exists(scriptFile), "deleting script metadata preserves the disk file");
    Verify(runRecordService.GetRecent(scriptId, 20).Count == 0, "deleting script cascades run history");
    foreach (var remainingScript in scriptService.GetAll())
        scriptService.Delete(remainingScript.Id);
    interpreterService.Delete(interpreter.Id);
    pathService.Remove(storedPath.Id);
    Verify(interpreterService.GetAll().Count == 0 && pathService.GetAll().Count == 0,
        "interpreter and script directory removal");

    var recovery = new DatabaseRecoveryService(factory);

    if (!recovery.TryRepair())
    {
        failures++;
        Console.Error.WriteLine("FAIL: healthy database integrity check");
    }
    else
    {
        Console.WriteLine("PASS: healthy database integrity check");
    }

    var backupDirectory = recovery.BackupAndReset();
    var backupDatabase = Path.Combine(backupDirectory, "pyrunner.db");
    if (!File.Exists(backupDatabase) || File.Exists(factory.DatabasePath))
    {
        failures++;
        Console.Error.WriteLine("FAIL: database backup and reset");
    }
    else
    {
        Console.WriteLine("PASS: database backup and reset");
    }
}
finally
{
    var safePrefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PyRunner.Verification"))
        + Path.DirectorySeparatorChar;
    var resolved = Path.GetFullPath(temporaryRoot);
    if (resolved.StartsWith(safePrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
        Directory.Delete(resolved, recursive: true);
}

return failures == 0 ? 0 : 1;

static string FindSolutionRoot()
{
    foreach (var seed in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(seed);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PyRunner.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
    }

    throw new DirectoryNotFoundException("Could not locate PyRunner.sln");
}

static string? FindPythonExecutable()
{
    var explicitPath = Environment.GetEnvironmentVariable("PYRUNNER_PYTHON");
    if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        return Path.GetFullPath(explicitPath);

    var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        try
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), "python.exe");
            if (File.Exists(candidate) &&
                !candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(candidate);
        }
        catch
        {
            // Ignore malformed PATH entries.
        }
    }

    return null;
}

static string GetPythonVersion(string pythonPath)
{
    try
    {
        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(pythonPath);
        return info.ProductVersion ?? info.FileVersion ?? "unknown";
    }
    catch
    {
        return "unknown";
    }
}

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);
