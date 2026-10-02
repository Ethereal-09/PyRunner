namespace PyRunner.Services;

public enum ShellPage
{
    Scripts,
    Runs,
    ScheduledTasks,
    Help,
}

public interface IShellNavigationService
{
    ShellPage CurrentPage { get; }
    event EventHandler<ShellPage>? Navigated;
    void Navigate(ShellPage page);
}

/// <summary>主窗口内嵌页面导航的单一状态源；不改变现有标题栏与工作区布局。</summary>
public sealed class ShellNavigationService : IShellNavigationService
{
    public ShellPage CurrentPage { get; private set; } = ShellPage.Scripts;

    public event EventHandler<ShellPage>? Navigated;

    public void Navigate(ShellPage page)
    {
        CurrentPage = page;
        Navigated?.Invoke(this, page);
    }
}
