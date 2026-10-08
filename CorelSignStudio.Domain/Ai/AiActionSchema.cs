using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CorelSignStudio.Domain.Automation;

namespace CorelSignStudio.Domain.Ai;

/// <summary>
/// Everything the model is told about the action vocabulary is derived here from the Domain's own
/// <see cref="CorelAction"/> types, so the prompt and the schema can never drift from what the executor
/// accepts: adding an action type or a property changes both automatically.
/// </summary>
public static class AiActionSchema
{
    public static readonly IReadOnlyList<string> StatusValues = ["ready", "needs_clarification", "cannot_do"];

    private static readonly NullabilityInfoContext Nullability = new();

    /// <summary>Property descriptions per action type, keyed by the JSON discriminator (for example <c>move</c>).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ActionProperty>> Actions { get; } = ActionTypes.All
        .OrderBy(ActionTypes.NameOf, StringComparer.Ordinal)
        .ToDictionary(ActionTypes.NameOf, type => (IReadOnlyList<ActionProperty>)PropertiesOf(type), StringComparer.Ordinal);

    /// <summary>The JSON property names a given action type accepts (camelCase), including <c>type</c> and <c>id</c>.</summary>
    public static IReadOnlySet<string> AllowedProperties(string actionType) =>
        Actions.TryGetValue(actionType, out var properties)
            ? properties.Select(property => property.Name).Append("type").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Human-readable reference of every action for the system prompt.</summary>
    public static string DescribeForPrompt()
    {
        var builder = new StringBuilder();
        foreach (var (name, properties) in Actions)
        {
            builder.Append("- ").Append(name).Append(": ");
            builder.AppendJoin(", ", properties.Where(property => property.Name != "id" && property.Name != "note").Select(property =>
                property.Name + (property.Required ? "*" : "") + ":" + property.TypeText));
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// A JSON Schema for the planner's answer. Action items are one flat object holding the union of all
    /// action properties with a closed <c>type</c> enum; per-type rules are enforced afterwards by
    /// <see cref="AiPlanParser"/> and <see cref="AutomationPlan.Validate"/>, which is where safety comes from.
    /// </summary>
    public static string BuildResponseSchema()
    {
        var actionProperties = new JsonObject
        {
            ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Actions.Keys.Select(key => (JsonNode)key).ToArray()) },
        };
        foreach (var property in Actions.Values.SelectMany(properties => properties).Where(property => property.Name != "note"))
        {
            actionProperties.TryAdd(property.Name, property.Schema.DeepClone());
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("status", "explanation", "clarificationQuestion", "confidence", "warnings", "name", "description", "target", "actions"),
            ["properties"] = new JsonObject
            {
                ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(StatusValues.Select(value => (JsonNode)value).ToArray()) },
                ["explanation"] = new JsonObject { ["type"] = "string" },
                ["clarificationQuestion"] = new JsonObject { ["type"] = "string" },
                ["confidence"] = new JsonObject { ["type"] = "number" },
                ["warnings"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["name"] = new JsonObject { ["type"] = "string" },
                ["description"] = new JsonObject { ["type"] = "string" },
                ["target"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("activeDocument", "newDocument") },
                ["actions"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("type"),
                        ["properties"] = actionProperties,
                    },
                },
            },
        };
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static List<ActionProperty> PropertiesOf(Type actionType)
    {
        var result = new List<ActionProperty>();
        foreach (var property in actionType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null || !property.CanWrite)
            {
                continue;
            }

            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null ||
                           (!property.PropertyType.IsValueType && Nullability.Create(property).WriteState == NullabilityState.Nullable);
            var required = property.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>() is not null && name != "id";
            var (typeText, schema) = Describe(Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType);
            result.Add(new ActionProperty(name, required, nullable, typeText, schema));
        }

        return result.OrderByDescending(property => property.Required).ThenBy(property => property.Name, StringComparer.Ordinal).ToList();
    }

    private static (string Text, JsonObject Schema) Describe(Type type)
    {
        if (type == typeof(string))
        {
            return ("string", new JsonObject { ["type"] = "string" });
        }

        if (type == typeof(bool))
        {
            return ("boolean", new JsonObject { ["type"] = "boolean" });
        }

        if (type == typeof(int) || type == typeof(long))
        {
            return ("integer", new JsonObject { ["type"] = "integer" });
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return ("number", new JsonObject { ["type"] = "number" });
        }

        if (type.IsEnum)
        {
            var names = Enum.GetNames(type).Select(JsonNamingPolicy.CamelCase.ConvertName).ToArray();
            return (string.Join("|", names), new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(names.Select(name => (JsonNode)name).ToArray()) });
        }

        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var element = type.IsArray ? type.GetElementType()! : type.GetGenericArguments().FirstOrDefault() ?? typeof(string);
            var (text, schema) = Describe(element);
            return (text + "[]", new JsonObject { ["type"] = "array", ["items"] = schema });
        }

        return ("object", new JsonObject { ["type"] = "object" });
    }
}

/// <param name="Name">camelCase JSON name.</param>
/// <param name="Required">Must be present.</param>
/// <param name="Nullable">May be omitted.</param>
/// <param name="TypeText">Short type description for the prompt.</param>
/// <param name="Schema">JSON Schema fragment.</param>
public sealed record ActionProperty(string Name, bool Required, bool Nullable, string TypeText, JsonObject Schema);
