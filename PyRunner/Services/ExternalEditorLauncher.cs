namespace PyRunner.Services;

public sealed class ExternalEditorLauncher : IExternalEditorLauncher
{
    public async Task<bool> OpenAsync(string filePath)
    {
        try
        {
            var normalized = Path.GetFullPath(filePath);
            if (!File.Exists(normalized) || !normalized.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) return false;
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(normalized);
            return await Windows.System.Launcher.LaunchFileAsync(file);
        }
        catch { return false; }
    }
}
