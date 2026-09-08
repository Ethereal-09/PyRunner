using Windows.ApplicationModel.DataTransfer;

namespace PyRunner.Services;

public interface IClipboardService
{
    void CopyText(string text);
}

public sealed class ClipboardService : IClipboardService
{
    public void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
