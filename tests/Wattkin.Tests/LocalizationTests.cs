using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using Wattkin.Settings;
using Wattkin.UI;
using Xunit;

namespace Wattkin.Tests;

/// <summary>The interface's two languages: Localization\Strings.resx (English) and Strings.fr.resx.</summary>
public sealed partial class LocalizationTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo(UiLanguage.French);

    private static Dictionary<string, string> Texts(CultureInfo culture, bool tryParents) =>
        Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents)!
            .Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

    private static Dictionary<string, string> English => Texts(CultureInfo.InvariantCulture, tryParents: true);

    /// <summary>Null when the French satellite assembly is missing from the build.</summary>
    private static Dictionary<string, string>? FrenchOnly =>
        Strings.ResourceManager.GetResourceSet(French, createIfNotExists: true, tryParents: false) is { } set
            ? set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!)
            : null;

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();

    private static string Holes(string text) => string.Join(",", Placeholder().Matches(text).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order());

    [Fact]
    public void EveryTextHasAFrenchTranslationAndNothingElse()
    {
        var french = FrenchOnly;
        Assert.NotNull(french); // the fr satellite assembly is built and found
        Assert.Empty(English.Keys.Except(french.Keys));
        Assert.Empty(french.Keys.Except(English.Keys));
        Assert.All(french, e => Assert.False(string.IsNullOrWhiteSpace(e.Value), e.Key));
    }

    [Fact]
    public void BothLanguagesFillTheSamePlaceholders()
    {
        var french = FrenchOnly!;
        Assert.All(English, e => Assert.True(Holes(e.Value) == Holes(french[e.Key]), $"{e.Key}: \"{e.Value}\" vs \"{french[e.Key]}\""));
    }

    [Fact]
    public void EveryTextFormatsWithoutThrowing()
    {
        // A stray brace or a placeholder past the arguments would only show up as a crash in a window.
        var args = Enumerable.Range(1, 6).Cast<object>().ToArray(); // integers suit every specifier used (0.0, X8, +0.00;-0.00;0)
        foreach (var texts in new[] { English, FrenchOnly! })
        {
            Assert.All(texts, e => string.Format(CultureInfo.InvariantCulture, e.Value, args));
        }
    }

    [Fact]
    public void FrenchPunctuationKeepsItsNonBreakingSpace()
    {
        // French puts a space before : ; ? ! % and °C; a breaking one can leave the sign alone at the start of a line.
        Assert.All(FrenchOnly!, e => Assert.DoesNotMatch(@" [:;?!%»]| °C|« ", e.Value));
    }

    [Fact]
    public void TheLanguageFollowsWindowsUnlessTheUserPickedOne()
    {
        Assert.Equal(UiLanguage.French, UiLanguage.Valid(" FR "));
        Assert.Equal(UiLanguage.English, UiLanguage.Valid("en"));
        Assert.Null(UiLanguage.Valid("de"));
        Assert.Null(UiLanguage.Valid(null));

        var settings = new AppSettings { Language = "klingon" };
        settings.Normalize();
        Assert.Null(settings.Language); // a hand-edited file falls back to Windows' language
    }

    [Fact]
    public void FrenchTextsComeFromTheFrenchFile()
    {
        // Read directly: switching UiLanguage here would race the tests that check English texts.
        Assert.Equal("Profil : {0}", Strings.ResourceManager.GetString(nameof(Strings.Osd_Profile), French));
        Assert.Equal("{0} h {1:00}", Strings.ResourceManager.GetString(nameof(Strings.Unit_HoursMinutes), French));
        Assert.Equal("1h 05m", Fmt.Duration(TimeSpan.FromMinutes(65))); // the tests run in English
    }
}
