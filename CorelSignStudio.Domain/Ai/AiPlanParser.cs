using System.Text.Json;
using System.Text.Json.Nodes;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Inspection;
using CorelSignStudio.Domain.Localization;
using CorelSignStudio.Domain.Planning;

namespace CorelSignStudio.Domain.Ai;

/// <summary>What the parser made of a model answer.</summary>
public sealed record AiParseResult
{
    public required AiPlanningStatus Status { get; init; }
    public AutomationPlan? Plan { get; init; }
    public string? Explanation { get; init; }
    public string? ClarificationQuestion { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public double? Confidence { get; init; }

    /// <summary>Why the answer was rejected, in the user's language.</summary>
    public string? UserError { get; init; }

    /// <summary>The same reasons in English, sent back to the model when a repair attempt is made.</summary>
    public string? RepairHint { get; init; }
}

/// <summary>
/// Turns the model's JSON into an <see cref="AutomationPlan"/> — or refuses to. This is the safety gate:
/// nothing the model writes is trusted. Unknown action types, unknown properties, shape ids that are not
/// in the inspected document and plans that fail <see cref="AutomationPlan.Validate"/> are all rejected,
/// so an invalid answer can never reach the executor.
/// </summary>
public sealed class AiPlanParser(Func<string, bool>? fileExists = null)
{
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

    public AiParseResult Parse(string content, PlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(ExtractJson(content ?? ""), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) as JsonObject
                   ?? throw new JsonException("The answer is not a JSON object.");
        }
        catch (JsonException exception)
        {
            return Invalid(Msg.Get("Ai.Invalid.Malformed"), "Your answer was not a single valid JSON object: " + exception.Message);
        }

        var explanation = Text(root, "explanation");
        var confidence = Number(root, "confidence") is { } raw ? Math.Clamp(raw, 0, 1) : (double?)null;
        var warnings = (root["warnings"] as JsonArray)?.Select(node => node?.ToString()).Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text!).ToList() ?? [];
        var status = (Text(root, "status") ?? "").Trim().ToLowerInvariant();

        if (status == "needs_clarification")
        {
            var question = Text(root, "clarificationQuestion");
            return string.IsNullOrWhiteSpace(question)
                ? Invalid(Msg.Get("Ai.Invalid.ClarificationEmpty"), "status was needs_clarification but clarificationQuestion was empty.")
                : new AiParseResult
                {
                    Status = AiPlanningStatus.NeedsClarification,
                    ClarificationQuestion = question.Trim(),
                    Explanation = explanation,
                    Confidence = confidence,
                    Warnings = warnings,
                };
        }

        if (status == "cannot_do")
        {
            // A considered "no" is a final answer, not something to repair.
            return new AiParseResult
            {
                Status = AiPlanningStatus.Invalid,
                Explanation = explanation,
                UserError = Msg.Format("Ai.Invalid.CannotDo", string.IsNullOrWhiteSpace(explanation) ? "—" : explanation),
            };
        }

        if (status != "ready")
        {
            return Invalid(Msg.Get("Ai.Invalid.Malformed"), $"status must be one of {string.Join(", ", AiActionSchema.StatusValues)} but was '{status}'.");
        }

        if (root["actions"] is not JsonArray actionNodes || actionNodes.Count == 0)
        {
            return Invalid(Msg.Get("Ai.Invalid.NoActions"), "status was ready but actions was empty.");
        }

        var actions = new List<CorelAction>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < actionNodes.Count; index++)
        {
            if (actionNodes[index] is not JsonObject node)
            {
                return Invalid(Msg.Format("Ai.Invalid.UnsupportedAction", index + 1, "?"), $"actions[{index}] is not an object.");
            }

            var type = Text(node, "type") ?? "";
            var canonicalType = AiActionSchema.Actions.Keys.FirstOrDefault(key => string.Equals(key, type, StringComparison.OrdinalIgnoreCase));
            if (canonicalType is null)
            {
                return Invalid(
                    Msg.Format("Ai.Invalid.UnsupportedAction", index + 1, type.Length == 0 ? "?" : type),
                    $"actions[{index}].type '{type}' is not a supported action type. Use only: {string.Join(", ", AiActionSchema.Actions.Keys)}.");
            }

            var allowed = AiActionSchema.AllowedProperties(canonicalType);
            var clean = new JsonObject { ["type"] = canonicalType };
            foreach (var (name, value) in node)
            {
                if (name.Equals("type", StringComparison.OrdinalIgnoreCase) || value is null)
                {
                    continue; // nulls come from schema-constrained output and simply mean "not set"
                }

                if (!allowed.Contains(name))
                {
                    return Invalid(
                        Msg.Format("Ai.Invalid.UnknownField", index + 1, canonicalType, name),
                        $"actions[{index}] ({canonicalType}) has an unknown property '{name}'. Allowed: {string.Join(", ", allowed.Order())}.");
                }

                clean[name] = value.DeepClone();
            }

            var id = Text(clean, "id");
            if (string.IsNullOrWhiteSpace(id) || !usedIds.Add(id))
            {
                id = NextFreeId(usedIds, index + 1);
                usedIds.Add(id);
            }

            clean["id"] = id;
            try
            {
                actions.Add(clean.Deserialize<CorelAction>(AutomationJson.Options) ?? throw new JsonException("empty action"));
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
            {
                return Invalid(
                    Msg.Format("Ai.Invalid.UnsupportedAction", index + 1, canonicalType),
                    $"actions[{index}] ({canonicalType}) could not be read: {exception.Message}");
            }
        }

        // Shape ids must come from the inspected document; nothing else may be targeted by id.
        var known = request.Document?.AllShapes().Select(shape => shape.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var target in actions.SelectMany(action => action.TargetRefs).Where(LogicalShapeId.IsLogicalId))
        {
            if (known is null)
            {
                return Invalid(Msg.Format("Ai.Invalid.NoDocumentForShape", target), $"'{target}' was targeted but no document was inspected.");
            }

            if (!known.Contains(target))
            {
                return Invalid(Msg.Format("Ai.Invalid.UnknownShape", target), $"'{target}' is not an id from the document context. Use only listed ids.");
            }
        }

        var plan = new AutomationPlan
        {
            Name = FirstNonEmpty(Text(root, "name"), Summarize(request.UserRequest)),
            Description = NullIfEmpty(Text(root, "description")) ?? NullIfEmpty(explanation),
            UserRequest = request.UserRequest,
            Target = actions[0].OpensDocument ? DocumentTarget.NewDocument : DocumentTarget.ActiveDocument,
            Actions = actions,
            ReferenceFiles = request.References,
            Parameters = request.Parameters,
            Metadata = new Dictionary<string, string> { ["planner"] = nameof(AiCommandPlanner) },
        };

        var validation = plan.Validate();
        if (!validation.IsValid)
        {
            return Invalid(
                Msg.Format("Ai.Invalid.Validation", string.Join("; ", validation.Errors)),
                "The plan failed validation: " + string.Join("; ", validation.Errors.Select(error => $"[{error.ActionId}] {error.Message}")));
        }

        // Warnings the application adds itself, whatever the model said.
        if (plan.HasDestructiveActions)
        {
            warnings.Add(Msg.Get("Ai.Warning.Destructive"));
        }

        foreach (var output in actions.OfType<OutputAction>().Where(output => SafeExists(output.FilePath)))
        {
            warnings.Add(Msg.Format("Ai.Warning.Overwrite", output.FilePath));
        }

        if (actions.Any(action => action.TargetRefs.Any(TargetRef.IsSelection)) && request.Document is { SelectedShapeIds.Count: 0 })
        {
            warnings.Add(Msg.Get("Ai.Warning.NothingSelected"));
        }

        return new AiParseResult
        {
            Status = AiPlanningStatus.Ready,
            Plan = plan,
            Explanation = explanation,
            Confidence = confidence,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>Accepts a bare object, or one wrapped in a Markdown fence or a sentence, and returns the object text.</summary>
    public static string ExtractJson(string content)
    {
        var start = content.IndexOf('{', StringComparison.Ordinal);
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }

    private static AiParseResult Invalid(string userError, string repairHint) =>
        new() { Status = AiPlanningStatus.Invalid, UserError = userError, RepairHint = repairHint };

    private static string NextFreeId(HashSet<string> used, int position)
    {
        var candidate = "a" + position;
        for (var suffix = 2; used.Contains(candidate); suffix++)
        {
            candidate = $"a{position}_{suffix}";
        }

        return candidate;
    }

    private bool SafeExists(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && _fileExists(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? Text(JsonObject node, string name)
    {
        var pair = node.FirstOrDefault(entry => entry.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        return pair.Value is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static double? Number(JsonObject node, string name) =>
        node[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? value.GetValue<double>() : null;

    private static string FirstNonEmpty(string? first, string second) => string.IsNullOrWhiteSpace(first) ? second : first.Trim();

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Summarize(string request)
    {
        var single = (request ?? "").ReplaceLineEndings(" ").Trim();
        return single.Length == 0 ? "Plan" : single.Length <= 60 ? single : single[..57] + "…";
    }
}
