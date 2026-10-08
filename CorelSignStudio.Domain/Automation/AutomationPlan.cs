using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CorelSignStudio.Domain.References;
using CorelSignStudio.Domain.Localization;

namespace CorelSignStudio.Domain.Automation;

public enum DocumentTarget
{
    /// <summary>Work on the document the user currently has open.</summary>
    ActiveDocument,

    /// <summary>The plan starts by creating or opening its own document.</summary>
    NewDocument,
}

public enum OutputFormat
{
    Cdr,
    Pdf,
    Png,
    Svg,
}

/// <summary>A file the plan is expected to produce; informational and used by the batch engine.</summary>
public sealed record OutputRequirement(OutputFormat Format, string? FilePath = null);

/// <summary>
/// An ordered, inspectable list of actions. Planners (deterministic today, AI later) produce plans;
/// the executor runs them. A plan is plain data and serializes cleanly to JSON.
/// </summary>
public sealed record AutomationPlan
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>The user's original wording, kept for history and future AI revision loops.</summary>
    public string? UserRequest { get; init; }

    public DocumentTarget Target { get; init; } = DocumentTarget.ActiveDocument;
    public IReadOnlyList<CorelAction> Actions { get; init; } = [];
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<ReferenceInput> ReferenceFiles { get; init; } = [];
    public IReadOnlyList<OutputRequirement> Outputs { get; init; } = [];
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    [JsonIgnore]
    public bool HasDestructiveActions => Actions.Any(action => action.IsDestructive);

    public string ToJson() => AutomationJson.Serialize(this);

    public static AutomationPlan FromJson(string json) => AutomationJson.Deserialize<AutomationPlan>(json);

    public PlanValidationResult Validate()
    {
        var errors = new List<PlanValidationError>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add(new(null, Msg.Get("Validation.PlanNameRequired")));
        }

        if (Actions.Count == 0)
        {
            errors.Add(new(null, Msg.Get("Validation.NoActions")));
        }

        if (Target == DocumentTarget.NewDocument && Actions.Count > 0 && !Actions[0].OpensDocument)
        {
            errors.Add(new(Actions[0].Id, Msg.Get("Validation.NewDocumentFirst")));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var creators = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in Actions)
        {
            if (action is null)
            {
                errors.Add(new(null, Msg.Get("Validation.EmptyAction")));
                continue;
            }

            foreach (var message in action.Validate())
            {
                errors.Add(new(action.Id, message));
            }

            if (!string.IsNullOrWhiteSpace(action.Id) && !seen.Add(action.Id))
            {
                errors.Add(new(action.Id, Msg.Format("Validation.DuplicateActionId", action.Id)));
            }

            foreach (var target in action.TargetRefs.Where(TargetRef.IsActionRef))
            {
                var referenced = TargetRef.ActionId(target);
                if (!creators.Contains(referenced))
                {
                    errors.Add(new(action.Id, seen.Contains(referenced) && referenced != action.Id
                        ? Msg.Format("Validation.ReferenceNotCreator", target)
                        : Msg.Format("Validation.ReferenceNotEarlier", target)));
                }
            }

            if (action.CreatesObjects && !string.IsNullOrWhiteSpace(action.Id))
            {
                creators.Add(action.Id);
            }
        }

        if (errors.Count == 0)
        {
            // Only reached for well-formed actions, so serializing cannot fail on non-finite numbers.
            foreach (var placeholder in PlaceholderSyntax.FindAll(AutomationJson.Serialize(Actions)))
            {
                errors.Add(new(null, Msg.Format("Validation.VariableNoValue", PlaceholderSyntax.Format(placeholder))));
            }
        }

        return new PlanValidationResult(errors);
    }
}

public sealed record PlanValidationError(string? ActionId, string Message)
{
    public override string ToString() => ActionId is null ? Message : $"[{ActionId}] {Message}";
}

public sealed record PlanValidationResult(IReadOnlyList<PlanValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public override string ToString() => IsValid ? Msg.Get("Validation.Valid") : string.Join(Environment.NewLine, Errors);
}

public static partial class PlaceholderSyntax
{
    /// <summary>Every variable name used by placeholders in <paramref name="text"/>, including inside expressions.</summary>
    public static IReadOnlyList<string> FindAll(string text) =>
        Pattern().Matches(text).SelectMany(match => Recipes.PlaceholderExpression.Variables(match.Groups[1].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string Format(string variableName) => "{{" + variableName + "}}";

    // The content is a variable name or a small arithmetic expression over names and numbers.
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_+\-*/(). ,]+?)\s*\}\}")]
    public static partial Regex Pattern();

    [GeneratedRegex(@"^\{\{\s*([A-Za-z0-9_+\-*/(). ,]+?)\s*\}\}$")]
    public static partial Regex WholeValuePattern();
}

/// <summary>The single JSON dialect used for plans, recipes, batch jobs, assets and history.</summary>
public static class AutomationJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"JSON did not contain a {typeof(T).Name}.");
}
