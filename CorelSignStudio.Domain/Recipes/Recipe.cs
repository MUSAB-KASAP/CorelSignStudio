using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CorelSignStudio.Domain.Automation;
using CorelSignStudio.Domain.Localization;

namespace CorelSignStudio.Domain.Recipes;

public enum RecipeVariableType
{
    Text,
    Number,
    Boolean,
}

public sealed record RecipeVariable
{
    /// <summary>Upper-case identifier used as <c>{{NAME}}</c> inside the plan template.</summary>
    public required string Name { get; init; }
    public RecipeVariableType Type { get; init; } = RecipeVariableType.Text;
    public string? DefaultValue { get; init; }
    public string? Description { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRequired => DefaultValue is null;
}

/// <summary>
/// A reusable automation: an <see cref="AutomationPlan"/> whose values may contain
/// <c>{{VARIABLE}}</c> placeholders. Recipes know nothing about what is being produced
/// (labels, tables, signs, serial-number jobs, …) — they only replay actions with new values.
/// </summary>
public sealed record Recipe
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<RecipeVariable> Variables { get; init; } = [];

    /// <summary>An AutomationPlan as JSON, with placeholders allowed in any value.</summary>
    public required JsonObject PlanTemplate { get; init; }

    public string? SourcePlanId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    public string ToJson() => AutomationJson.Serialize(this);

    public static Recipe FromJson(string json) => AutomationJson.Deserialize<Recipe>(json);

    /// <summary>Produces a concrete plan by filling in the variables.</summary>
    /// <param name="values">Values by variable name; names are case-insensitive. Undeclared names are allowed and treated as text.</param>
    /// <exception cref="RecipeVariableException">A required value is missing or has the wrong type.</exception>
    public AutomationPlan Instantiate(IReadOnlyDictionary<string, string>? values = null)
    {
        var resolved = ResolveValues(values);
        var types = Variables.ToDictionary(variable => variable.Name, variable => variable.Type, StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        var node = VariableSubstitution.Apply(PlanTemplate.DeepClone(), resolved, types, errors);
        if (errors.Count > 0)
        {
            throw new RecipeVariableException(Name, errors);
        }

        var root = node!.AsObject();
        root["id"] = Guid.NewGuid().ToString("N");
        root["createdUtc"] = JsonValue.Create(DateTimeOffset.UtcNow);
        root["parameters"] = JsonSerializer.SerializeToNode(resolved, AutomationJson.Options);

        if (root["metadata"] is not JsonObject metadata)
        {
            metadata = [];
            root["metadata"] = metadata;
        }

        metadata["recipeId"] = Id;
        metadata["recipeName"] = Name;

        try
        {
            return root.Deserialize<AutomationPlan>(AutomationJson.Options)
                   ?? throw new JsonException("The recipe template is empty.");
        }
        catch (JsonException exception)
        {
            throw new RecipeVariableException(Name, [Msg.Format("Recipe.NotValidPlan", exception.Message)]);
        }
    }

    /// <summary>Merges defaults with supplied values and reports what is still missing.</summary>
    public Dictionary<string, string> ResolveValues(IReadOnlyDictionary<string, string>? values)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in Variables.Where(variable => variable.DefaultValue is not null))
        {
            resolved[variable.Name] = variable.DefaultValue!;
        }

        if (values is not null)
        {
            foreach (var (name, value) in values)
            {
                if (value is not null)
                {
                    resolved[name.Trim()] = value;
                }
            }
        }

        return resolved;
    }
}

public sealed class RecipeVariableException(string recipeName, IReadOnlyList<string> errors)
    : Exception(Msg.Format("Recipe.CouldNotFill", recipeName, string.Join("; ", errors)))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Replaces <c>{{NAME}}</c> placeholders inside a JSON tree.</summary>
public static class VariableSubstitution
{
    /// <summary>Replaces placeholders in plain text; unknown names are reported in <paramref name="errors"/>.</summary>
    public static string Apply(string text, IReadOnlyDictionary<string, string> values, ICollection<string>? errors = null) =>
        PlaceholderSyntax.Pattern().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            if (TryGet(values, name, out var value))
            {
                return value;
            }

            Report(errors, Msg.Format("Recipe.VariableNoValue", name));
            return match.Value;
        });

    /// <summary>
    /// Replaces placeholders in every string value of <paramref name="node"/>. A value that is exactly one
    /// placeholder for a Number/Boolean variable becomes a real JSON number/boolean, so numeric fields
    /// such as <c>widthMm</c> can be variables too.
    /// </summary>
    public static JsonNode? Apply(
        JsonNode? node,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, RecipeVariableType>? types,
        ICollection<string> errors)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var key in jsonObject.Select(pair => pair.Key).ToArray())
                {
                    var child = jsonObject[key];
                    var replaced = Apply(child, values, types, errors);
                    if (!ReferenceEquals(child, replaced))
                    {
                        jsonObject[key] = replaced;
                    }
                }

                return jsonObject;

            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    var child = jsonArray[index];
                    var replaced = Apply(child, values, types, errors);
                    if (!ReferenceEquals(child, replaced))
                    {
                        jsonArray[index] = replaced;
                    }
                }

                return jsonArray;

            case JsonValue jsonValue when jsonValue.TryGetValue<string>(out var text) && text.Contains("{{", StringComparison.Ordinal):
                return ReplaceValue(text, values, types, errors);

            default:
                return node;
        }
    }

    private static JsonNode ReplaceValue(
        string text,
        IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, RecipeVariableType>? types,
        ICollection<string> errors)
    {
        var whole = PlaceholderSyntax.WholeValuePattern().Match(text);
        if (whole.Success)
        {
            var name = whole.Groups[1].Value;
            var type = RecipeVariableType.Text;
            types?.TryGetValue(name, out type);
            if (type != RecipeVariableType.Text && TryGet(values, name, out var raw))
            {
                switch (type)
                {
                    case RecipeVariableType.Number when TryParseNumber(raw, out var number):
                        return JsonValue.Create(number);
                    case RecipeVariableType.Boolean when TryParseBoolean(raw, out var boolean):
                        return JsonValue.Create(boolean);
                    default:
                        Report(errors, Msg.Format("Recipe.VariableWrongType", name, Msg.Get("Recipe.Type." + type), raw));
                        return JsonValue.Create(text);
                }
            }
        }

        return JsonValue.Create(Apply(text, values, errors));
    }

    public static bool TryParseNumber(string raw, out double number)
    {
        var trimmed = raw.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number))
        {
            return true;
        }

        // Spreadsheets in many locales (including Turkish) write 12,5 for 12.5.
        return !trimmed.Contains('.') &&
               double.TryParse(trimmed.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
               double.IsFinite(number);
    }

    public static bool TryParseBoolean(string raw, out bool value)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "yes" or "evet":
                value = true;
                return true;
            case "false" or "0" or "no" or "hayır" or "hayir":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool TryGet(IReadOnlyDictionary<string, string> values, string name, out string value)
    {
        if (values.TryGetValue(name, out value!))
        {
            return true;
        }

        foreach (var (key, candidate) in values)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = "";
        return false;
    }

    private static void Report(ICollection<string>? errors, string message)
    {
        if (errors is not null && !errors.Contains(message))
        {
            errors.Add(message);
        }
    }
}

/// <summary>Says "this literal value in the plan should become a variable".</summary>
/// <param name="VariableName">For example <c>PERSON_NAME</c>.</param>
/// <param name="Literal">The value as it appears in the successful plan, for example <c>Ahmet Yılmaz</c> or <c>500</c>.</param>
/// <param name="Type">Number bindings replace matching JSON numbers; text bindings replace occurrences inside strings.</param>
/// <param name="PropertyName">Optional JSON property (for example <c>widthMm</c>) to limit a Number binding to.</param>
public sealed record RecipeVariableBinding(
    string VariableName,
    string Literal,
    RecipeVariableType Type = RecipeVariableType.Text,
    string? PropertyName = null,
    string? Description = null);

public static class RecipeBuilder
{
    /// <summary>Turns a plan (typically one that just ran successfully) into a reusable recipe.</summary>
    public static Recipe FromPlan(
        AutomationPlan plan,
        string name,
        IEnumerable<RecipeVariableBinding>? bindings = null,
        string? description = null,
        string? category = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var template = JsonSerializer.SerializeToNode(plan, AutomationJson.Options)!.AsObject();
        template.Remove("parameters");
        var variables = new List<RecipeVariable>();

        foreach (var binding in bindings ?? [])
        {
            var variableName = NormalizeName(binding.VariableName);
            if (string.IsNullOrEmpty(binding.Literal))
            {
                throw new ArgumentException(Msg.Format("Recipe.BindingNeedsLiteral", variableName));
            }

            if (variables.Any(variable => variable.Name == variableName))
            {
                throw new ArgumentException(Msg.Format("Recipe.BoundTwice", variableName));
            }

            var placeholder = PlaceholderSyntax.Format(variableName);
            var replaced = binding.Type == RecipeVariableType.Text
                ? ReplaceText(template["actions"], binding.Literal, placeholder)
                : ReplaceScalar(template["actions"], binding, placeholder);
            if (binding.Type == RecipeVariableType.Text)
            {
                // Keep the plan's own wording in step with the variable ("...sign for {{PERSON_NAME}}").
                foreach (var key in new[] { "userRequest", "description" })
                {
                    if (template[key] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        template[key] = text.Replace(binding.Literal, placeholder, StringComparison.Ordinal);
                    }
                }
            }

            if (replaced == 0)
            {
                throw new ArgumentException(Msg.Format("Recipe.LiteralNotInPlan", binding.Literal, variableName));
            }

            variables.Add(new RecipeVariable
            {
                Name = variableName,
                Type = binding.Type,
                DefaultValue = binding.Literal,
                Description = binding.Description,
            });
        }

        // Placeholders the plan already contained (for example an output path) become variables too.
        foreach (var existing in PlaceholderSyntax.FindAll(template["actions"]?.ToJsonString() ?? ""))
        {
            var variableName = NormalizeName(existing);
            if (variables.All(variable => variable.Name != variableName))
            {
                variables.Add(new RecipeVariable { Name = variableName });
            }
        }

        return new Recipe
        {
            Name = name.Trim(),
            Description = description ?? plan.Description,
            Category = category,
            Variables = variables,
            PlanTemplate = template,
            SourcePlanId = plan.Id,
        };
    }

    public static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // "Ad Soyad" → AD_SOYAD, "{{person-name}}" → PERSON_NAME, "İsim" → ISIM.
        var ascii = Batch.FileNameSanitizer.Sanitize(name.Trim().Trim('{', '}'), "VAR");
        var cleaned = new string(ascii
            .Select(character => char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_')
            .ToArray());
        return char.IsAsciiDigit(cleaned[0]) ? "_" + cleaned : cleaned;
    }

    public static string NormalizeNameOrEmpty(string? name) => string.IsNullOrWhiteSpace(name) ? "" : NormalizeName(name);

    private static int ReplaceText(JsonNode? node, string literal, string placeholder)
    {
        var count = 0;
        Visit(node, null, (parent, key, index, value, _) =>
        {
            if (value.TryGetValue<string>(out var text) && text.Contains(literal, StringComparison.Ordinal))
            {
                Set(parent, key, index, JsonValue.Create(text.Replace(literal, placeholder, StringComparison.Ordinal)));
                count++;
            }
        });
        return count;
    }

    private static int ReplaceScalar(JsonNode? node, RecipeVariableBinding binding, string placeholder)
    {
        var count = 0;
        Visit(node, null, (parent, key, index, value, propertyName) =>
        {
            if (binding.PropertyName is not null &&
                !string.Equals(binding.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var matches = binding.Type switch
            {
                RecipeVariableType.Number =>
                    value.GetValueKind() == JsonValueKind.Number &&
                    VariableSubstitution.TryParseNumber(binding.Literal, out var expected) &&
                    Math.Abs(value.GetValue<double>() - expected) < 1e-9,
                RecipeVariableType.Boolean =>
                    value.GetValueKind() is JsonValueKind.True or JsonValueKind.False &&
                    VariableSubstitution.TryParseBoolean(binding.Literal, out var expected) &&
                    value.GetValue<bool>() == expected,
                _ => false,
            };
            if (matches)
            {
                Set(parent, key, index, JsonValue.Create(placeholder));
                count++;
            }
        });
        return count;
    }

    private static void Visit(
        JsonNode? node,
        string? propertyName,
        Action<JsonNode, string?, int, JsonValue, string?> onValue)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var key in jsonObject.Select(pair => pair.Key).ToArray())
                {
                    // Ids and type discriminators are structure, not content.
                    if (key is "id" or "type")
                    {
                        continue;
                    }

                    if (jsonObject[key] is JsonValue value)
                    {
                        onValue(jsonObject, key, -1, value, key);
                    }
                    else
                    {
                        Visit(jsonObject[key], key, onValue);
                    }
                }

                break;

            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    if (jsonArray[index] is JsonValue value)
                    {
                        // Target references inside "targets" arrays are structure as well.
                        if (propertyName != "targets")
                        {
                            onValue(jsonArray, null, index, value, propertyName);
                        }
                    }
                    else
                    {
                        Visit(jsonArray[index], propertyName, onValue);
                    }
                }

                break;
        }
    }

    private static void Set(JsonNode parent, string? key, int index, JsonNode replacement)
    {
        if (parent is JsonObject jsonObject)
        {
            jsonObject[key!] = replacement;
        }
        else
        {
            parent.AsArray()[index] = replacement;
        }
    }
}

public interface IRecipeStore
{
    IReadOnlyList<Recipe> GetAll();

    Recipe? Get(string id);

    Recipe? FindByName(string name);

    void Save(Recipe recipe);

    bool Delete(string id);
}
