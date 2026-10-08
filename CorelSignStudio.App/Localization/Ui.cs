using System.Resources;
using System.Windows.Markup;
using CorelSignStudio.Domain.Localization;

namespace CorelSignStudio.App;

/// <summary>
/// The window's own texts (labels, buttons, hints, status and activity messages) from <c>Ui.resx</c>.
/// The neutral language is Turkish; adding <c>Ui.en-US.resx</c> and setting <see cref="Msg.Culture"/>
/// at start-up is all an English version needs. View models use <see cref="T"/>/<see cref="F"/>;
/// XAML uses <c>{local:Loc Some.Key}</c>.
/// </summary>
public static class Ui
{
    private static readonly ResourceManager Resources = new("CorelSignStudio.App.Ui", typeof(Ui).Assembly);

    /// <summary>Returns the text for <paramref name="key"/>, or <c>[key]</c> so a missing text is obvious.</summary>
    public static string T(string key) => Resources.GetString(key, Msg.Culture) ?? $"[{key}]";

    public static string F(string key, params object?[] arguments) => string.Format(Msg.Culture, T(key), arguments);
}

/// <summary>XAML access to <see cref="Ui"/>: <c>Text="{local:Loc Tab.Operator}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    [ConstructorArgument("key")]
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Ui.T(Key);
}
