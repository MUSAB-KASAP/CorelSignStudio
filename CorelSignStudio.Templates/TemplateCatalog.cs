using CorelSignStudio.Domain;

namespace CorelSignStudio.Templates;

/// <summary>
/// Template registration point. Concrete MVP templates are added after the
/// Corel connection milestone is verified.
/// </summary>
public sealed class TemplateCatalog
{
    private readonly Dictionary<string, Func<DesignSpec>> _factories = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Names => _factories.Keys;

    public void Register(string name, Func<DesignSpec> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(factory);
        _factories.Add(name, factory);
    }

    public DesignSpec Create(string name) =>
        _factories.TryGetValue(name, out var factory)
            ? factory()
            : throw new KeyNotFoundException($"Template '{name}' is not registered.");
}

