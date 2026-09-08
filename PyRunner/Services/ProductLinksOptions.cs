namespace PyRunner.Services;

/// <summary>产品外链的唯一配置入口。未配置的入口由帮助页隐藏。</summary>
public sealed record ProductLinksOptions(
    Uri? ProjectHomepage = null,
    Uri? IssueTracker = null,
    Uri? ReleasesPage = null,
    Uri? AuthorHomepage = null,
    Uri? LicensePage = null,
    Uri? UpdateFeed = null);

public interface IExternalLinkService
{
    Task<bool> OpenAsync(Uri uri);
}

public sealed class ExternalLinkService : IExternalLinkService
{
    public async Task<bool> OpenAsync(Uri uri)
    {
        try
        {
            return await Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            _ = ex;
#if DEBUG
            DebugLog.WriteLine($"Help: failed to open external URI ({ex.Message})");
#endif
            return false;
        }
    }
}
