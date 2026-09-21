using PyRunner.Models;

namespace PyRunner.Services.AI;

public sealed class AiConfigurationService
{
    private readonly ISettingsService _settings;
    public AiConfigurationService(ISettingsService settings) => _settings = settings;

    public bool IsEnabled => _settings.Current.AiEnabled;

    public AiConfiguration GetCurrent()
    {
        var settings = _settings.Current;
        var provider = Enum.TryParse<AiProviderKind>(settings.AiProvider, true, out var parsed)
            ? parsed : AiProviderKind.OpenAI;
        var endpoint = AiEndpointPolicy.Normalize(settings.AiEndpoint, provider);
        return new AiConfiguration(provider, endpoint, settings.AiModel.Trim(),
            Math.Clamp(settings.AiTimeoutSeconds, 10, 300));
    }
}
