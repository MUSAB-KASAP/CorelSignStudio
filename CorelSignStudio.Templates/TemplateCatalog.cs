namespace CorelSignStudio.Templates;

public sealed class TemplateCatalog
{
    private readonly Dictionary<string, ISignTemplate> _templates;

    public TemplateCatalog(IEnumerable<ISignTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        _templates = templates.ToDictionary(template => template.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ISignTemplate> Templates => _templates.Values.OrderBy(template => template.Name).ToArray();

    public IReadOnlyList<ISignTemplate> GetByCategory(SignCategory category) =>
        Templates.Where(template => template.Category == category).ToArray();

    public ISignTemplate Get(string id) =>
        _templates.TryGetValue(id, out var template)
            ? template
            : throw new KeyNotFoundException($"Template '{id}' is not registered.");

    public static TemplateCatalog CreateDefault() => new([new ProhibitionSignTemplate()]);
}

