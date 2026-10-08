using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.Planning;

namespace CorelSignStudio.Domain.Ai;

public enum PlannerMode
{
    /// <summary>Use the AI planner when it is configured, otherwise the built-in one.</summary>
    Ai,

    /// <summary>Always use the built-in test planner.</summary>
    Deterministic,
}

/// <summary>
/// Chooses the planner for each request. The built-in <see cref="DeterministicCommandPlanner"/> is always
/// available: it is used when the user selects it, when no AI provider is configured, and as a fallback
/// when the provider cannot be reached and the built-in planner understands the whole request.
/// </summary>
public sealed class PlannerRouter(
    ICommandPlanner builtIn,
    Func<IAiCommandPlanner?> aiPlannerFactory,
    Func<PlannerMode> mode)
{
    /// <summary>True when the next request will go to the AI planner.</summary>
    public bool IsAiActive => mode() == PlannerMode.Ai && aiPlannerFactory() is not null;

    public string ActivePlannerName => IsAiActive ? Msg.Get("Ai.Planner.Name") : builtIn.Name;

    public async Task<AiPlanningResult> PlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
    {
        var aiPlanner = mode() == PlannerMode.Ai ? aiPlannerFactory() : null;
        if (aiPlanner is null)
        {
            return await PlanWithBuiltInAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var result = await aiPlanner.PlanWithAiAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Status != AiPlanningStatus.ProviderError)
        {
            return result;
        }

        // The provider is down: keep the user working if the built-in planner fully understands the request.
        var fallback = await builtIn.PlanAsync(request, cancellationToken).ConfigureAwait(false);
        return fallback.Success
            ? new AiPlanningResult
            {
                Status = AiPlanningStatus.Ready,
                Plan = fallback.Plan,
                UserMessage = fallback.Message ?? "",
                Warnings = [Msg.Format("Ai.Warning.FallbackUsed", result.UserMessage)],
                Diagnostics = result.Diagnostics with { PlannerUsed = nameof(DeterministicCommandPlanner) },
            }
            : result;
    }

    private async Task<AiPlanningResult> PlanWithBuiltInAsync(PlanningRequest request, CancellationToken cancellationToken)
    {
        var result = await builtIn.PlanAsync(request, cancellationToken).ConfigureAwait(false);
        return new AiPlanningResult
        {
            Status = result.Plan is null ? AiPlanningStatus.Invalid : AiPlanningStatus.Ready,
            Plan = result.Plan,
            UserMessage = result.Message ?? "",
            Warnings = result.UnrecognizedCommands.Count == 0
                ? []
                : [Msg.Format("Ai.Warning.NotUnderstood", string.Join(" | ", result.UnrecognizedCommands))],
            Diagnostics = new AiPlanningDiagnostics { PlannerUsed = nameof(DeterministicCommandPlanner), Attempts = 1 },
        };
    }
}

/// <summary>
/// Optional request/response logging around any <see cref="IAiClient"/>. API keys never pass through an
/// <see cref="AiRequest"/>, so they cannot be logged here. Document content is logged only while
/// <paramref name="enabled"/> returns true; otherwise just sizes and outcome are recorded.
/// </summary>
public sealed class LoggingAiClient(IAiClient inner, Action<string> log, Func<bool> enabled) : IAiClient
{
    public string ProviderId => inner.ProviderId;

    public string Model => inner.Model;

    public bool SupportsImages => inner.SupportsImages;

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var verbose = enabled();
        log($"AI request → {inner.ProviderId}/{inner.Model}: system {request.SystemPrompt.Length} chars, user {request.UserMessage.Length} chars, schema {(request.JsonSchema is null ? "no" : "yes")}.");
        // Images are described by metadata only — never their bytes, in any logging mode.
        foreach (var image in request.Images)
        {
            log("AI request image: " + image.Describe());
        }

        if (verbose)
        {
            log("AI request body (debug):\n" + request.UserMessage);
        }

        try
        {
            var response = await inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
            log($"AI response ← id={response.RequestId ?? "?"}, {response.Content.Length} chars, tokens in/out {response.InputTokens?.ToString() ?? "?"}/{response.OutputTokens?.ToString() ?? "?"}.");
            if (verbose)
            {
                log("AI response body (debug):\n" + response.Content);
            }

            return response;
        }
        catch (AiClientException exception)
        {
            log($"AI error ← {exception.Kind}: {exception.Message}");
            throw;
        }
    }
}
