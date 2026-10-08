using System.Text;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.Planning;

namespace CorelSignStudio.Domain.Ai;

/// <summary>
/// Natural-language planner: user request + document context → validated <see cref="Automation.AutomationPlan"/>.
/// It only produces plans. Execution stays with the existing executor, after the user has seen and
/// approved the plan.
/// </summary>
public sealed class AiCommandPlanner(
    IAiClient client,
    IDocumentContextBuilder? contextBuilder = null,
    AiPlanParser? parser = null,
    AiPlannerOptions? options = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IAiCommandPlanner
{
    private static readonly string SystemPrompt = AiSystemPrompt.Build();
    private static readonly string ResponseSchema = AiActionSchema.BuildResponseSchema();

    private readonly IDocumentContextBuilder _contextBuilder = contextBuilder ?? new DocumentContextBuilder();
    private readonly AiPlanParser _parser = parser ?? new AiPlanParser();
    private readonly AiPlannerOptions _options = options ?? new AiPlannerOptions();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public string Name => Msg.Get("Ai.Planner.Name");

    public async Task<PlanningResult> PlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
    {
        var result = await PlanWithAiAsync(request, cancellationToken).ConfigureAwait(false);
        return new PlanningResult { Plan = result.Plan, Message = result.UserMessage };
    }

    public async Task<AiPlanningResult> PlanWithAiAsync(PlanningRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.UserRequest))
        {
            return new AiPlanningResult { Status = AiPlanningStatus.Invalid, UserMessage = Msg.Get("Planner.TypeRequest") };
        }

        var context = _contextBuilder.Build(request.Document, _options);
        var baseMessage = BuildUserMessage(request, context);
        var userMessage = baseMessage;
        var attempts = 0;
        var repaired = false;
        AiResponse? response = null;
        AiParseResult? parsed = null;

        while (attempts < Math.Max(1, _options.MaxAttempts))
        {
            attempts++;
            try
            {
                response = await client.CompleteAsync(
                    new AiRequest
                    {
                        SystemPrompt = SystemPrompt,
                        UserMessage = userMessage,
                        JsonSchema = _options.UseJsonSchema ? ResponseSchema : null,
                        MaxOutputTokens = _options.MaxOutputTokens,
                        Timeout = _options.RequestTimeout,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AiClientException exception)
            {
                // A small, bounded retry for failures that are not the request's fault. Never a loop.
                if (exception.IsTransient && attempts < _options.MaxAttempts)
                {
                    await _delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return new AiPlanningResult
                {
                    Status = AiPlanningStatus.ProviderError,
                    UserMessage = exception.UserMessage,
                    Diagnostics = Diagnostics(response, attempts) with { ErrorKind = exception.Kind, TechnicalError = exception.ToString() },
                };
            }

            parsed = _parser.Parse(response.Content, request);
            if (parsed.Status != AiPlanningStatus.Invalid || parsed.RepairHint is null || repaired || attempts >= _options.MaxAttempts)
            {
                break;
            }

            // One chance to correct a rejected answer, with the reason spelled out.
            repaired = true;
            userMessage = baseMessage +
                          "\n\n## Your previous answer was rejected\n\n" + parsed.RepairHint +
                          "\n\nPrevious answer:\n" + Truncate(response.Content, 4000) +
                          "\n\nReturn a corrected JSON object. If the request cannot be planned correctly, " +
                          "return needs_clarification or cannot_do instead of guessing.";
        }

        var diagnostics = Diagnostics(response, attempts);
        if (parsed is null)
        {
            return new AiPlanningResult { Status = AiPlanningStatus.Invalid, UserMessage = Msg.Get("Ai.Invalid.Malformed"), Diagnostics = diagnostics };
        }

        var warnings = parsed.Warnings.ToList();
        if (context.WasClipped && parsed.Status == AiPlanningStatus.Ready)
        {
            warnings.Add(Msg.Format("Ai.Warning.ContextClipped", context.DetailedShapeIds.Count, context.BriefShapeIds.Count + context.OmittedShapeCount));
        }

        if (parsed.Status == AiPlanningStatus.Ready && parsed.Confidence is { } confidence && confidence < _options.LowConfidenceThreshold)
        {
            warnings.Add(Msg.Get("Ai.Warning.LowConfidence"));
        }

        return new AiPlanningResult
        {
            Status = parsed.Status,
            Plan = parsed.Plan,
            Explanation = parsed.Explanation,
            ClarificationQuestion = parsed.ClarificationQuestion,
            Confidence = parsed.Confidence,
            Warnings = warnings,
            UserMessage = parsed.Status switch
            {
                AiPlanningStatus.Ready => Msg.Format("Planner.Prepared", parsed.Plan!.Actions.Count),
                AiPlanningStatus.NeedsClarification => parsed.ClarificationQuestion!,
                _ => parsed.UserError ?? Msg.Get("Ai.Invalid.Malformed"),
            },
            Diagnostics = diagnostics,
        };
    }

    /// <summary>The request-specific part of the prompt: document, references, earlier turns, and the request itself.</summary>
    public string BuildUserMessage(PlanningRequest request, DocumentContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Document context").AppendLine().AppendLine(context.Text).AppendLine();
        builder.AppendLine(_contextBuilder.DescribeReferences(request.References)).AppendLine();

        if (request.Conversation.Count > 0)
        {
            builder.AppendLine("## Earlier in this conversation").AppendLine();
            foreach (var turn in request.Conversation)
            {
                builder.Append("User: ").AppendLine(turn.UserText);
                if (!string.IsNullOrWhiteSpace(turn.PlannerQuestion))
                {
                    builder.Append("You asked: ").AppendLine(turn.PlannerQuestion);
                }
            }

            builder.AppendLine()
                .AppendLine("The message below answers your question. Plan the original request using that answer.")
                .AppendLine();
        }

        builder.AppendLine("## Request").AppendLine().Append(request.UserRequest.Trim());
        return builder.ToString();
    }

    private AiPlanningDiagnostics Diagnostics(AiResponse? response, int attempts) => new()
    {
        ProviderId = response?.ProviderId ?? client.ProviderId,
        Model = response?.Model ?? client.Model,
        RequestId = response?.RequestId,
        Attempts = attempts,
        InputTokens = response?.InputTokens,
        OutputTokens = response?.OutputTokens,
        PlannerUsed = nameof(AiCommandPlanner),
    };

    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength] + "…";
}
