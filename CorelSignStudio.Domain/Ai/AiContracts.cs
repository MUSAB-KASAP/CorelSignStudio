using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.Planning;

namespace CorelSignStudio.Domain.Ai;

/// <summary>One provider-neutral completion request: a system prompt, one user message, optionally a JSON schema.</summary>
public sealed record AiRequest
{
    public required string SystemPrompt { get; init; }
    public required string UserMessage { get; init; }

    /// <summary>Images the model should look at (multimodal providers only). Their bytes are never logged.</summary>
    public IReadOnlyList<AiImage> Images { get; init; } = [];

    /// <summary>JSON Schema the answer must follow, for providers that can enforce one. Optional.</summary>
    public string? JsonSchema { get; init; }

    public int MaxOutputTokens { get; init; } = 16000;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);
}

/// <summary>An image attached to a request. Only the metadata may appear in logs.</summary>
public sealed record AiImage
{
    public required string FileName { get; init; }

    /// <summary>image/png or image/jpeg.</summary>
    public required string MimeType { get; init; }

    public required byte[] Bytes { get; init; }
    public int WidthPixels { get; init; }
    public int HeightPixels { get; init; }

    /// <summary>Safe one-line description for logs: name, type, pixel size and byte count — never the content.</summary>
    public string Describe() => $"{FileName} ({MimeType}, {WidthPixels}x{HeightPixels} px, {Bytes.Length} bytes)";
}

public sealed record AiResponse
{
    public required string Content { get; init; }
    public string? ProviderId { get; init; }
    public string? Model { get; init; }
    public string? RequestId { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
}

/// <summary>
/// The only thing the application knows about an AI provider. Implementations live outside the Domain
/// (see CorelSignStudio.AI) so that no provider SDK is referenced here.
/// </summary>
public interface IAiClient
{
    /// <summary>Stable technical id of the provider, for example <c>anthropic</c>.</summary>
    string ProviderId { get; }

    string Model { get; }

    /// <summary>True when <see cref="AiRequest.Images"/> is honoured.</summary>
    bool SupportsImages => false;

    /// <exception cref="AiClientException">Any provider, network or configuration failure.</exception>
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default);
}

public enum AiErrorKind
{
    Unknown,
    NotConfigured,
    InvalidApiKey,
    RateLimited,
    Timeout,
    Network,
    ServiceUnavailable,
    BadRequest,
    ModelNotFound,
    Refused,
}

/// <summary>A provider failure, already classified so the UI can explain it without knowing the provider.</summary>
public sealed class AiClientException(AiErrorKind kind, string technicalMessage, Exception? innerException = null)
    : Exception(technicalMessage, innerException)
{
    public AiErrorKind Kind { get; } = kind;

    /// <summary>Worth one more attempt: the request itself was fine.</summary>
    public bool IsTransient => Kind is AiErrorKind.RateLimited or AiErrorKind.Timeout or AiErrorKind.Network or AiErrorKind.ServiceUnavailable;

    /// <summary>The Turkish (or other configured language) sentence to show the user. Never contains technical detail.</summary>
    public string UserMessage => AiUserMessages.For(Kind);
}

public static class AiUserMessages
{
    public static string For(AiErrorKind kind) => Msg.Get("Ai.Error." + kind);
}

public sealed record AiPlannerOptions
{
    /// <summary>Shapes described in full detail; the rest are listed by id and type only.</summary>
    public int MaxDetailedShapes { get; init; } = 150;

    /// <summary>Approximate upper bound for the document part of the prompt (characters, roughly 4 per token).</summary>
    public int MaxContextCharacters { get; init; } = 40000;

    /// <summary>Total provider calls for one request, counting retries after transient errors and one repair of a rejected answer.</summary>
    public int MaxAttempts { get; init; } = 3;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxOutputTokens { get; init; } = 16000;

    /// <summary>Send the JSON schema to providers that can enforce it.</summary>
    public bool UseJsonSchema { get; init; } = true;

    /// <summary>Below this the user is warned to review the plan carefully.</summary>
    public double LowConfidenceThreshold { get; init; } = 0.6;
}

public enum AiPlanningStatus
{
    /// <summary>A valid plan is ready for the user to review.</summary>
    Ready,

    /// <summary>The request is ambiguous; <see cref="AiPlanningResult.ClarificationQuestion"/> must be answered first.</summary>
    NeedsClarification,

    /// <summary>The answer could not be turned into a valid plan. Nothing can be executed.</summary>
    Invalid,

    /// <summary>The provider could not be reached or refused. Nothing can be executed.</summary>
    ProviderError,
}

public sealed record AiPlanningDiagnostics
{
    public string? ProviderId { get; init; }
    public string? Model { get; init; }
    public string? RequestId { get; init; }
    public int Attempts { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public AiErrorKind? ErrorKind { get; init; }
    public string? TechnicalError { get; init; }
    public string? PlannerUsed { get; init; }
}

/// <summary>The outcome of planning. A plan is present only when <see cref="Status"/> is Ready.</summary>
public sealed record AiPlanningResult
{
    public required AiPlanningStatus Status { get; init; }
    public AutomationPlan? Plan { get; init; }

    /// <summary>What the planner intends to do, in the user's language.</summary>
    public string? Explanation { get; init; }

    public string? ClarificationQuestion { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>0..1 as reported by the planner, when it reports one.</summary>
    public double? Confidence { get; init; }

    /// <summary>The one sentence to show the user for this outcome.</summary>
    public required string UserMessage { get; init; }

    public AiPlanningDiagnostics Diagnostics { get; init; } = new();

    public bool IsReady => Status == AiPlanningStatus.Ready && Plan is not null;
}

/// <summary>A planner backed by a language model. It only ever produces plans; it never touches CorelDRAW.</summary>
public interface IAiCommandPlanner : ICommandPlanner
{
    Task<AiPlanningResult> PlanWithAiAsync(PlanningRequest request, CancellationToken cancellationToken = default);
}
