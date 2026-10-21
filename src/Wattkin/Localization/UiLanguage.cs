using System.Globalization;

namespace Wattkin;

/// <summary>
/// The interface language, English or French: <c>Localization\Strings.resx</c> and <c>Strings.fr.resx</c>,
/// read through the generated <see cref="Strings"/> class. It follows Windows' display language unless the
/// user picked one (<see cref="Settings.AppSettings.Language"/>). Numbers and dates keep Windows' regional
/// format whatever the language. Windows are built in one language, so <see cref="App.SetLanguage"/>
/// rebuilds the open ones after a change. Logs, the self-test and diagnostics reports stay in English.
/// </summary>
internal static class UiLanguage
{
    public const string English = "en";
    public const string French = "fr";

    /// <summary>Windows' display language as this process started with it (<see cref="Strings"/> never changes it).</summary>
    private static readonly CultureInfo Windows = CultureInfo.CurrentUICulture;

    /// <summary>What "Same as Windows" stands for: French on a French Windows, English otherwise.</summary>
    public static string WindowsLanguage => Windows.TwoLetterISOLanguageName == French ? French : English;

    /// <summary>The language in use.</summary>
    public static string Current { get; private set; } = WindowsLanguage;

    /// <summary>A language a settings file may name, or null (Windows' language) for anything else.</summary>
    public static string? Valid(string? language) => language?.Trim().ToLowerInvariant() is (English or French) and var valid ? valid : null;

    /// <param name="language">"en", "fr", or null for Windows' language.</param>
    public static void Apply(string? language)
    {
        Current = Valid(language) ?? WindowsLanguage;
        Strings.Culture = CultureInfo.GetCultureInfo(Current);
    }

    /// <summary>A language's name in that language, as language lists show them.</summary>
    public static string NameOf(string language) => language == French ? "Français" : "English";
}
