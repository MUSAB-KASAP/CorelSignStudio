using CorelSignStudio.AI;
using CorelSignStudio.Domain.Ai;

namespace CorelSignStudio.App;

/// <summary>
/// The application's live AI configuration: current settings, where the key comes from, and the planner
/// built from them. The planner is rebuilt only when settings change. The key itself is never exposed.
/// </summary>
public sealed class AiRuntime(AiSettingsStore store, Action<string> log)
{
    private readonly object _gate = new();
    private AiSettings _settings = store.Load();
    private IAiCommandPlanner? _planner;
    private bool _plannerBuilt;

    public AiSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    public IReadOnlyList<AiProviderInfo> Providers => AiProviders.All;

    public ApiKeySource KeySource => store.GetApiKeySource(Settings.Provider);

    public bool IsConfigured => store.IsConfigured(Settings);

    public string SettingsFilePath => store.FilePath;

    /// <summary>The AI planner, or <c>null</c> while no provider is fully configured.</summary>
    public IAiCommandPlanner? Planner
    {
        get
        {
            lock (_gate)
            {
                if (!_plannerBuilt)
                {
                    _planner = CreateClient() is { } client ? new AiCommandPlanner(client) : null;
                    _plannerBuilt = true;
                }

                return _planner;
            }
        }
    }

    /// <param name="settings">New non-secret settings.</param>
    /// <param name="newApiKey">A newly typed key; <c>null</c> or empty keeps the stored one.</param>
    public void Save(AiSettings settings, string? newApiKey)
    {
        lock (_gate)
        {
            store.Save(settings);
            if (!string.IsNullOrWhiteSpace(newApiKey))
            {
                store.SaveApiKey(settings.Provider, newApiKey);
            }

            _settings = settings;
            _plannerBuilt = false;
        }

        log($"AI settings saved: provider={settings.Provider}, model={settings.Model}, planner={settings.PlannerMode}, debug={settings.DebugLogging}, key={KeySource}.");
    }

    public void RemoveStoredApiKey()
    {
        lock (_gate)
        {
            store.SaveApiKey(_settings.Provider, null);
            _plannerBuilt = false;
        }

        log("Stored AI API key removed.");
    }

    /// <summary>Sends a minimal request to prove the provider, model and key work together.</summary>
    /// <exception cref="AiClientException">With a classified reason when the test fails.</exception>
    public async Task<AiResponse> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var client = CreateClient() ?? throw new AiClientException(AiErrorKind.NotConfigured, "No provider, model or key is configured.");
        return await client.CompleteAsync(
            new AiRequest
            {
                SystemPrompt = "You are a connectivity check. Reply with the single word OK.",
                UserMessage = "OK?",
                MaxOutputTokens = 256,
                Timeout = TimeSpan.FromSeconds(45),
            },
            cancellationToken);
    }

    private IAiClient? CreateClient()
    {
        var settings = Settings;
        if (!store.IsConfigured(settings))
        {
            return null;
        }

        return new LoggingAiClient(
            AiProviders.CreateClient(settings, store.GetApiKey(settings.Provider)),
            log,
            () => Settings.DebugLogging);
    }
}
