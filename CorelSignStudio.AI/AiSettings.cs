using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CorelSignStudio.Domain.Ai;

namespace CorelSignStudio.AI;

/// <summary>Non-secret AI preferences. The API key is deliberately not part of this record.</summary>
public sealed record AiSettings
{
    public string Provider { get; init; } = AnthropicAiClient.Provider;
    public string Model { get; init; } = AnthropicAiClient.DefaultModel;
    public PlannerMode PlannerMode { get; init; } = PlannerMode.Ai;

    /// <summary>When true, full prompts and answers are written to the log file (never the API key).</summary>
    public bool DebugLogging { get; init; }
}

public sealed record AiProviderInfo(string Id, string DisplayName, string DefaultModel, string ApiKeyEnvironmentVariable);

/// <summary>The providers this build can talk to. Adding one means adding an <see cref="IAiClient"/> and a line here.</summary>
public static class AiProviders
{
    public static IReadOnlyList<AiProviderInfo> All { get; } =
    [
        new(AnthropicAiClient.Provider, "Anthropic (Claude)", AnthropicAiClient.DefaultModel, "ANTHROPIC_API_KEY"),
    ];

    public static AiProviderInfo Get(string? id) =>
        All.FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <exception cref="AiClientException">The provider is unknown or the key is missing.</exception>
    public static IAiClient CreateClient(AiSettings settings, string? apiKey)
    {
        var provider = Get(settings.Provider);
        return provider.Id switch
        {
            AnthropicAiClient.Provider => new AnthropicAiClient(apiKey ?? "", settings.Model),
            _ => throw new AiClientException(AiErrorKind.NotConfigured, $"Unknown AI provider '{settings.Provider}'."),
        };
    }
}

/// <summary>Encrypts and decrypts a secret for the current user.</summary>
public interface ISecretProtector
{
    string Protect(string secret);

    /// <returns>The secret, or <c>null</c> when it cannot be decrypted (other user, other machine, damaged data).</returns>
    string? Unprotect(string protectedSecret);
}

/// <summary>
/// Windows DPAPI, scoped to the signed-in Windows user: the stored value is useless to another user or
/// on another computer, and no password has to be managed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CorelSignStudio.AI.ApiKey.v1");

    public string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    public string? Unprotect(string protectedSecret)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

public enum ApiKeySource
{
    None,
    Stored,
    EnvironmentVariable,
}

/// <summary>
/// Persists AI settings in the user's local application data folder — outside the repository — with the
/// API key encrypted by <see cref="ISecretProtector"/>. A key in the provider's environment variable
/// (for example ANTHROPIC_API_KEY) is used when no key is stored. The key is never written in plain text.
/// </summary>
public sealed class AiSettingsStore(string filePath, ISecretProtector protector, Func<string, string?>? readEnvironment = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Func<string, string?> _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
    private readonly object _gate = new();

    public string FilePath { get; } = Path.GetFullPath(filePath);

    /// <summary>%LOCALAPPDATA%\CorelSignStudio\ai-settings.json</summary>
    public static string DefaultFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CorelSignStudio", "ai-settings.json");

    public AiSettings Load() => Read().Settings ?? new AiSettings();

    public void Save(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            Write(Read() with { Settings = settings });
        }
    }

    /// <summary>Stores the key encrypted. An empty value removes the stored key.</summary>
    public void SaveApiKey(string providerId, string? apiKey)
    {
        lock (_gate)
        {
            var file = Read();
            var keys = new Dictionary<string, string>(file.ProtectedApiKeys ?? [], StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                keys.Remove(providerId);
            }
            else
            {
                keys[providerId] = protector.Protect(apiKey.Trim());
            }

            Write(file with { ProtectedApiKeys = keys });
        }
    }

    /// <summary>The stored key if there is one, otherwise the provider's environment variable.</summary>
    public string? GetApiKey(string providerId) => ResolveApiKey(providerId).Key;

    public ApiKeySource GetApiKeySource(string providerId) => ResolveApiKey(providerId).Source;

    public bool IsConfigured(AiSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.Model) && GetApiKeySource(settings.Provider) != ApiKeySource.None;

    private (string? Key, ApiKeySource Source) ResolveApiKey(string providerId)
    {
        var stored = Read().ProtectedApiKeys?.FirstOrDefault(pair => string.Equals(pair.Key, providerId, StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrWhiteSpace(stored) && protector.Unprotect(stored) is { Length: > 0 } key)
        {
            return (key, ApiKeySource.Stored);
        }

        var fromEnvironment = _readEnvironment(AiProviders.Get(providerId).ApiKeyEnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnvironment) ? (null, ApiKeySource.None) : (fromEnvironment.Trim(), ApiKeySource.EnvironmentVariable);
    }

    private SettingsFile Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(FilePath, Encoding.UTF8), JsonOptions) ?? new SettingsFile()
                : new SettingsFile();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsFile(); // a damaged settings file must not stop the application from starting
        }
    }

    private void Write(SettingsFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(file, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, FilePath, overwrite: true);
    }

    private sealed record SettingsFile
    {
        public AiSettings? Settings { get; init; }

        /// <summary>Provider id → encrypted key (base64 of the protected bytes).</summary>
        public Dictionary<string, string>? ProtectedApiKeys { get; init; }
    }
}
