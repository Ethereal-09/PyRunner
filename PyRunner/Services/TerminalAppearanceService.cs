namespace PyRunner.Services;

public interface ITerminalAppearanceService
{
    int FontSize { get; }
    event Action<int>? FontSizeChanged;
    void SetFontSize(int fontSize);
}

public sealed class TerminalAppearanceService : ITerminalAppearanceService
{
    public const int MinimumFontSize = 10;
    public const int MaximumFontSize = 24;
    private readonly ISettingsService _settings;

    public TerminalAppearanceService(ISettingsService settings)
    {
        _settings = settings;
        var clamped = Math.Clamp(settings.Current.TerminalFontSize, MinimumFontSize, MaximumFontSize);
        if (clamped != settings.Current.TerminalFontSize)
        {
            settings.Update(value => value.TerminalFontSize = clamped);
#if DEBUG
            DebugLog.WriteLine("TerminalAppearance: invalid persisted font size was clamped");
#endif
        }
    }

    public int FontSize => Math.Clamp(_settings.Current.TerminalFontSize, MinimumFontSize, MaximumFontSize);
    public event Action<int>? FontSizeChanged;

    public void SetFontSize(int fontSize)
    {
        var clamped = Math.Clamp(fontSize, MinimumFontSize, MaximumFontSize);
        if (FontSize == clamped) return;
        _settings.Update(value => value.TerminalFontSize = clamped);
        FontSizeChanged?.Invoke(clamped);
    }
}
