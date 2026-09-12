using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PyRunner.Data;
using PyRunner.Helpers;
using PyRunner.Models;
using PyRunner.Services;
using PyRunner.ViewModels;
using PyRunner.Views;

namespace PyRunner;

/// <summary>
/// 应用程序入口。Windows App SDK 运行时由 NuGet 注入的自动初始化器
/// （DeploymentManager 自动初始化，默认开启）在进程启动时完成初始化。
/// 同时负责构建全局 DI 容器（M1 骨架）：数据层 / 服务层单例 + VM 与对话框按次创建。
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private OnboardingWindow? _onboardingWindow;
    private bool _transitioningFromOnboarding;
    private bool _servicesDisposed;
    private const int CurrentOnboardingVersion = 1;

    internal XamlRoot? CurrentXamlRoot => _window?.Content?.XamlRoot;

    /// <summary>全局服务容器（OnLaunched 前构造完成）。
    /// 保留具体类型 ServiceProvider：窗口关闭后 Dispose 级联释放 IDisposable 单例。</summary>
    public ServiceProvider Services { get; }

    public App()
    {
        InitializeComponent();
#if DEBUG
        // 诊断钩子：绑定/资源解析失败与未处理异常全部落盘，避免 UIA 冒烟时盲区
        UnhandledException += (_, e) => DebugLog.WriteLine($"App: UnhandledException handled={e.Handled} {e.Exception}");
        DebugSettings.BindingFailed += (_, a) => DebugLog.WriteLine($"App: BindingFailed {a.Message}");
        DebugSettings.XamlResourceReferenceFailed += (_, a) => DebugLog.WriteLine($"App: XamlResourceReferenceFailed {a.Message}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DebugLog.WriteLine($"App: AppDomain.UnhandledException terminating={e.IsTerminating} {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) => DebugLog.WriteLine($"App: UnobservedTaskException {e.Exception}");
#endif
        Services = ConfigureServices();
    }

    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 数据层
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<DatabaseRecoveryService>();

        // 服务层（接口 + 实现分离，单例）
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<IStartupService, StartupService>();
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<IScriptService, ScriptService>();
        services.AddSingleton<IInterpreterService, InterpreterService>();
        services.AddSingleton<IAppMetadataService, AppMetadataService>();
        services.AddSingleton(new ProductLinksOptions(
            ProjectHomepage: new Uri("https://github.com/Ethereal-09/PyRunner"),
            IssueTracker: new Uri("https://github.com/Ethereal-09/PyRunner/issues"),
            ReleasesPage: new Uri("https://github.com/Ethereal-09/PyRunner/releases"),
            AuthorHomepage: new Uri("https://github.com/Ethereal-09"),
            UpdateFeed: new Uri("https://ethereal-09.github.io/PyRunner/update.json")));
        services.AddSingleton(_ =>
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                { Timeout = TimeSpan.FromSeconds(8) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PyRunner");
            return client;
        });
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IUiDispatcher>(_ => new DispatcherQueueUiDispatcher(
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IExternalLinkService, ExternalLinkService>();
        services.AddSingleton<GitHubPagesUpdateManifestService>();
        services.AddSingleton<IUpdateService>(provider =>
            provider.GetRequiredService<GitHubPagesUpdateManifestService>());
        services.AddSingleton(_ => TrustedUpdateHttpClient.CreateDefault());
        services.AddSingleton<IUpdateReleaseAssetService>(provider =>
            provider.GetRequiredService<GitHubPagesUpdateManifestService>());
        services.AddSingleton<IUpdatePackageDownloader, UpdatePackageDownloader>();
        services.AddSingleton<IUpdatePackageVerifier, Sha256UpdatePackageVerifier>();
        services.AddSingleton<IUpdateInstallConfirmationService, WinUiUpdateInstallConfirmationService>();
        services.AddSingleton<IInstallerLauncher, InstallerLauncher>();
        services.AddSingleton<IVerifiedUpdateInstaller, VerifiedUpdateInstaller>();
        services.AddSingleton<IApplicationExitService, ApplicationExitService>();
        services.AddSingleton<IUpdateCacheStore, SettingsUpdateCacheStore>();
        services.AddSingleton<IUpdateStateStore, UpdateStateStore>();
        services.AddSingleton<UpdateCheckCoordinator>();
        services.AddSingleton<IShellNavigationService, ShellNavigationService>();
        services.AddSingleton<IScriptPathService, ScriptPathService>();
        services.AddSingleton<IRunRecordService, RunRecordService>();
        services.AddSingleton<IScheduledTaskService, ScheduledTaskService>();
        services.AddSingleton<ScheduleCoordinator>();

        // Phase E：目录监听（单例，窗口关闭后随容器 Dispose 回收 watcher）与友好通知
        services.AddSingleton<IScriptDirectoryWatcher, ScriptDirectoryWatcher>();
        services.AddSingleton<INotificationService, NotificationService>();

        // 运行编排（Phase D）：窗口生命周期内单实例，协调全部运行中标签
        services.AddSingleton<RunCoordinator>();
        services.AddSingleton<IUpdateInstallGuard, UpdateInstallGuard>();

        // ViewModel / 对话框（按次创建，避免状态串用）
        services.AddTransient<MainViewModel>();
        services.AddTransient<ScriptListViewModel>();
        services.AddTransient<ScriptEditViewModel>();
        services.AddTransient<ScriptEditDialog>();
        // Phase C：文件树与设置弹窗
        services.AddTransient<FileTreeViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsDialog>();
        // 运行历史与独立首次启动窗口
        services.AddTransient<RunHistoryViewModel>();
        services.AddTransient<ScheduledTasksViewModel>();
        services.AddTransient<ScheduledTasksPage>();
        services.AddTransient<ScheduledTaskEditDialog>();
        services.AddTransient<HelpAboutViewModel>();
        services.AddTransient<HelpAboutPage>();
        services.AddTransient<OnboardingWindow>();
        // 对话框工厂：窗口强类型注入，替代 IServiceProvider locator
        services.AddTransient<Func<ScriptEditDialog>>(sp => () => sp.GetRequiredService<ScriptEditDialog>());
        services.AddTransient<Func<SettingsDialog>>(sp => () => sp.GetRequiredService<SettingsDialog>());
        services.AddTransient<Func<HelpAboutPage>>(sp => () => sp.GetRequiredService<HelpAboutPage>());
        services.AddTransient<Func<ScheduledTasksPage>>(sp => () => sp.GetRequiredService<ScheduledTasksPage>());
        services.AddTransient<Func<ScheduledTaskEditDialog>>(sp => () => sp.GetRequiredService<ScheduledTaskEditDialog>());

        return services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 启动即迁移：幂等，二次启动不重复建表。
        // 失败时记录日志并弹错误对话框（文案走资源键）后退出，
        // 禁止异常裸抛导致「双击无反应」。
        try
        {
            Services.GetRequiredService<SchemaMigrator>().Migrate();
        }
        catch (Exception ex)
        {
            ShowDatabaseRecovery(ex);
            return;
        }

        LaunchInitialWindow();
    }

    private void LaunchInitialWindow()
    {
        var settings = Services.GetRequiredService<ISettingsService>().Current;
        if (settings.FirstRunCompleted && settings.FirstRunVersion >= CurrentOnboardingVersion)
            CreateAndActivateMainWindow();
        else
            CreateAndActivateOnboardingWindow();
    }

    private void CreateAndActivateOnboardingWindow()
    {
        try
        {
            _onboardingWindow = Services.GetRequiredService<OnboardingWindow>();
            _onboardingWindow.Completed += OnOnboardingCompleted;
            _onboardingWindow.ExitRequested += OnOnboardingExitRequested;
            _window = _onboardingWindow;
            _onboardingWindow.Activate();
        }
        catch (Exception ex)
        {
            ShowStartupErrorAndExit(ex);
        }
    }

    private void CreateAndActivateMainWindow()
    {
        // 主窗口强类型注入：全部依赖由容器解析传入（Phase B 起不再传 IServiceProvider）。
        // 构造链已变长（DI 解析 + Sidebar.Initialize + 列表 Load），包 try/catch 兜底，
        // 失败走 ShowStartupErrorAndExit，杜绝「双击无反应」
        try
        {
            _window = new MainWindow(
                Services.GetRequiredService<IScriptService>(),
                Services.GetRequiredService<IScriptPathService>(),
                Services.GetRequiredService<ISettingsService>(),
                Services.GetRequiredService<ILocalizationService>(),
                Services.GetRequiredService<MainViewModel>(),
                Services.GetRequiredService<ScriptListViewModel>(),
                Services.GetRequiredService<FileTreeViewModel>(),
                Services.GetRequiredService<RunCoordinator>(),
                Services.GetRequiredService<Func<ScriptEditDialog>>(),
                Services.GetRequiredService<Func<SettingsDialog>>(),
                Services.GetRequiredService<INotificationService>(),
                Services.GetRequiredService<IScriptDirectoryWatcher>(),
                Services.GetRequiredService<RunHistoryViewModel>(),
                Services.GetRequiredService<IShellNavigationService>(),
                Services.GetRequiredService<Func<HelpAboutPage>>(),
                Services.GetRequiredService<UpdateCheckCoordinator>(),
                Services.GetRequiredService<IScheduledTaskService>(),
                Services.GetRequiredService<ScheduleCoordinator>(),
                Services.GetRequiredService<Func<ScheduledTasksPage>>(),
                Services.GetRequiredService<Func<ScheduledTaskEditDialog>>());
        }
        catch (Exception ex)
        {
            ShowStartupErrorAndExit(ex);
            return;
        }

        // 窗口关闭后 Dispose 容器：级联释放 IDisposable 单例
        // （如 JsonSettingsService 内部先 Flush 末次落盘再停 timer），替代纯手工 Flush 约定。
        // 隐性契约：本订阅必须晚于 MainWindow.OnClosed 执行 —— Closed 事件按订阅顺序分发，
        // MainWindow 构造内先订阅 OnClosed（设置刷盘/会话释放依赖尚未 Dispose 的单例），
        // 此处后订阅；调整订阅顺序会破坏该契约
        _window.Closed += (_, _) => DisposeServicesOnce();

        _window.Activate();
    }

    private async void OnOnboardingCompleted(object? sender, OnboardingCompletedEventArgs args)
    {
        if (_transitioningFromOnboarding || _onboardingWindow == null) return;
        _transitioningFromOnboarding = true;

        try
        {
            await Task.Run(() => PersistOnboardingDraft(args.Draft));
        }
        catch (Exception ex)
        {
            _ = ex;
            _transitioningFromOnboarding = false;
            _onboardingWindow.ShowCompletionError(
                Services.GetRequiredService<ILocalizationService>()["Wizard_SaveFailed"]);
#if DEBUG
            DebugLog.WriteLine($"App: 首次引导配置保存失败（{ex}）");
#endif
            return;
        }

        var onboarding = _onboardingWindow;
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            settings.Update(current =>
            {
                current.FirstRunCompleted = true;
                current.FirstRunVersion = CurrentOnboardingVersion;
            });
            settings.FlushOrThrow();

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                throw new InvalidOperationException("无法确定 PyRunner 可执行文件路径");

            // Windows App SDK 1.5 在同一进程内从独立引导 Window 切换到
            // MainWindow 时会在 Microsoft.ui.xaml.dll 中触发原生访问冲突。
            // 完成标记已落盘，因此由新进程通过正常启动门控直接创建主窗口。
            using var restartedProcess = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
            });
            if (restartedProcess == null)
                throw new InvalidOperationException("无法重新启动 PyRunner");
        }
        catch (Exception ex)
        {
            _ = ex;
            RollBackOnboardingCompletion();
            _transitioningFromOnboarding = false;
            onboarding.ShowCompletionError(
                Services.GetRequiredService<ILocalizationService>()["Wizard_SaveFailed"]);
#if DEBUG
            DebugLog.WriteLine($"App: 完成引导后重启失败（{ex}）");
#endif
            return;
        }

        onboarding.Completed -= OnOnboardingCompleted;
        onboarding.ExitRequested -= OnOnboardingExitRequested;
        onboarding.AllowCloseAndClose();
        _onboardingWindow = null;
        _window = null;
        DisposeServicesOnce();
        Exit();
    }

    private void RollBackOnboardingCompletion()
    {
        try
        {
            var settings = Services.GetRequiredService<ISettingsService>();
            settings.Update(current =>
            {
                current.FirstRunCompleted = false;
                current.FirstRunVersion = 0;
            });
            settings.Flush();
        }
        catch { }
    }

    private void PersistOnboardingDraft(OnboardingDraft draft)
    {
        var pathService = Services.GetRequiredService<IScriptPathService>();
        foreach (var path in draft.ScriptDirectories)
        {
            try { pathService.Add(path); }
            catch (ValidationException ex) when (ex.LocalizationKey == "Error_PathDuplicate") { }
        }

        // 自动刷新开关只控制后续启动/监听；首次确认的目录必须立即完成一次导入。
        if (draft.ScriptDirectories.Count > 0)
            Services.GetRequiredService<IScriptService>().ImportFromPaths(draft.ScriptDirectories);

        if (!string.IsNullOrWhiteSpace(draft.InterpreterPath))
        {
            var interpreterService = Services.GetRequiredService<IInterpreterService>();
            var interpreter = interpreterService.AddFromPath(draft.InterpreterPath);
            interpreterService.SetDefault(interpreter.Id);
        }

        var settings = Services.GetRequiredService<ISettingsService>();
        settings.Update(current =>
        {
            current.AutoRefreshScripts = draft.AutoRefreshScripts;
            current.FirstRunCompleted = false;
            current.FirstRunVersion = 0;
        });
        settings.FlushOrThrow();
    }

    private void OnOnboardingExitRequested(object? sender, EventArgs args)
    {
        if (_onboardingWindow == null) return;
        _onboardingWindow.Completed -= OnOnboardingCompleted;
        _onboardingWindow.ExitRequested -= OnOnboardingExitRequested;
        _onboardingWindow.AllowCloseAndClose();
        _onboardingWindow = null;
        _window = null;
        DisposeServicesOnce();
        Exit();
    }

    private void DisposeServicesOnce()
    {
        if (_servicesDisposed) return;
        _servicesDisposed = true;
        Services.Dispose();
    }

    /// <summary>数据库启动失败：提供「修复」与「备份后重建」，成功后继续正常启动。</summary>
    private void ShowDatabaseRecovery(Exception originalException)
    {
#if DEBUG
        DebugLog.WriteLine($"App: 数据库初始化失败，进入恢复流程（{originalException}）");
#endif
        var localization = Services.GetRequiredService<ILocalizationService>();
        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        var host = new Window { Content = root };

        root.Loaded += OnHostLoaded;
        host.Activate();

        async void OnHostLoaded(object sender, RoutedEventArgs args)
        {
            root.Loaded -= OnHostLoaded;
            try
            {
                while (true)
                {
                    var dialog = new ContentDialog
                    {
                        Title = localization["Error_Database_Title"],
                        Content = localization["Error_Database_Content"],
                        PrimaryButtonText = localization["Button_Repair"],
                        SecondaryButtonText = localization["Button_BackupRebuild"],
                        CloseButtonText = localization["Button_Exit"],
                        DefaultButton = ContentDialogButton.Primary,
                    };
                    DialogHostHelper.Prepare(dialog, root.XamlRoot, ElementTheme.Dark);

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.None)
                    {
                        try { host.Close(); } catch { }
                        Services.Dispose();
                        Exit();
                        return;
                    }

                    try
                    {
                        var recovery = Services.GetRequiredService<DatabaseRecoveryService>();
                        if (result == ContentDialogResult.Primary)
                        {
                            if (!recovery.TryRepair())
                                throw new InvalidOperationException("integrity_check failed");
                        }
                        else
                        {
                            var backupPath = recovery.BackupAndReset();
#if DEBUG
                            DebugLog.WriteLine($"App: 数据库已备份并重建，备份目录={backupPath}");
#endif
                        }

                        Services.GetRequiredService<SchemaMigrator>().Migrate();
                        host.Close();
                        LaunchInitialWindow();
                        return;
                    }
                    catch (Exception ex)
                    {
                        _ = ex;
#if DEBUG
                        DebugLog.WriteLine($"App: 数据库恢复失败（{ex}）");
#endif
                        var failed = new ContentDialog
                        {
                            Title = localization["Error_Database_Title"],
                            Content = localization["Error_Database_RecoveryFailed"],
                            CloseButtonText = localization["Button_OK"],
                        };
                        DialogHostHelper.Prepare(failed, root.XamlRoot, ElementTheme.Dark);
                        await failed.ShowAsync();
                    }
                }
            }
            catch (Exception dialogException)
            {
                _ = dialogException;
#if DEBUG
                DebugLog.WriteLine($"App: 数据库恢复界面失败（{dialogException}）");
#endif
                try { host.Close(); } catch { }
                Services.Dispose();
                Exit();
            }
        }
    }

    /// <summary>启动阶段致命错误：日志 + 错误对话框（资源键文案）+ 退出。</summary>
    private void ShowStartupErrorAndExit(Exception ex)
    {
#if DEBUG
        DebugLog.WriteLine($"App: 启动阶段失败（{ex}）");
#endif

        string title, content, okText;
        try
        {
            var localization = Services.GetRequiredService<ILocalizationService>();
            title = localization["Error_AppInitFailed_Title"];
            content = localization["Error_AppInitFailed"];
            okText = localization["Button_OK"];
        }
        catch
        {
            // 本地化服务自身不可用时退化为键名，保证对话框仍能弹出
            title = "Error_AppInitFailed_Title";
            content = "Error_AppInitFailed";
            okText = "OK";
        }

#if DEBUG
        content += $"\n\n{ex}";
#endif

        // 启动失败时主窗口尚未创建：临时窗口承载 ContentDialog。
        // XamlRoot 只有在内容进入可视树后才可用；等待 Loaded 再弹窗。
        var root = new Grid { RequestedTheme = ElementTheme.Dark };
        var host = new Window { Content = root };
        root.Loaded += OnHostLoaded;
        host.Activate();

        async void OnHostLoaded(object sender, RoutedEventArgs args)
        {
            root.Loaded -= OnHostLoaded;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = content,
                    CloseButtonText = okText,
                    DefaultButton = ContentDialogButton.Primary,
                };
                DialogHostHelper.Prepare(dialog, root.XamlRoot, ElementTheme.Dark);
                await dialog.ShowAsync();
            }
            catch (Exception dialogException)
            {
                _ = dialogException;
#if DEBUG
                DebugLog.WriteLine($"App: 启动错误弹窗显示失败（{dialogException}）");
#endif
            }
            finally
            {
                try { host.Close(); } catch { }
                Services.Dispose();
                Exit();
            }
        }
    }
}
