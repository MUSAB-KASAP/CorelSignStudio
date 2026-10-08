using System.Globalization;
using System.Resources;

[assembly: NeutralResourcesLanguage("tr-TR")]

namespace CorelSignStudio.Domain.Localization;

/// <summary>
/// Central access to user-facing text produced outside the UI layer (plan step descriptions, validation
/// and execution messages, planner feedback). Texts live in <c>Messages.resx</c>; the neutral language is
/// Turkish. To add English, add <c>Messages.en-US.resx</c> and set <see cref="Culture"/> at start-up.
/// Type names, JSON contracts and technical log lines are deliberately not localized.
/// </summary>
public static class Msg
{
    public const string DefaultCultureName = "tr-TR";

    private static readonly ResourceManager Resources =
        new("CorelSignStudio.Domain.Localization.Messages", typeof(Msg).Assembly);

    private static CultureInfo _culture = CultureInfo.GetCultureInfo(DefaultCultureName);

    /// <summary>The language and number/date format of everything shown to the user.</summary>
    public static CultureInfo Culture
    {
        get => _culture;
        set => _culture = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Returns the text for <paramref name="key"/>, or <c>[key]</c> so a missing text is obvious.</summary>
    public static string Get(string key) => Resources.GetString(key, _culture) ?? $"[{key}]";

    public static bool Has(string key) => Resources.GetString(key, _culture) is not null;

    public static string Format(string key, params object?[] arguments) =>
        string.Format(_culture, Get(key), arguments);

    /// <summary>Formats a length, size or angle for display, for example 12,5 in Turkish.</summary>
    public static string Number(double value) => value.ToString("0.##", _culture);
}
