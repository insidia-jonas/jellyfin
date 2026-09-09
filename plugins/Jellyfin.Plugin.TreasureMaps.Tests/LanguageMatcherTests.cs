using System;
using Jellyfin.Plugin.TreasureMaps.Languages;
using Xunit;

namespace Jellyfin.Plugin.TreasureMaps.Tests;

public class LanguageMatcherTests
{
    private static LanguagePreferences Prefs(string? primary, string[] secondary, bool filter)
        => new LanguagePreferences(primary, secondary, filter);

    [Theory]
    [InlineData("German", "de")]
    [InlineData("deutsch", "de")]
    [InlineData("GER", "de")]
    [InlineData("en-US", "en")]
    [InlineData("English (US)", "en")]
    [InlineData("español", "es")]
    public void Normalize_MapsAliases(string input, string expected)
    {
        Assert.Equal(expected, LanguageMatcher.Normalize(input));
    }

    [Fact]
    public void Match_PrimaryLanguage_RankZero()
    {
        var prefs = Prefs("de", new[] { "en" }, false);
        var result = LanguageMatcher.Match(prefs, new[] { "English", "German" });

        Assert.True(result.Keep);
        Assert.Equal(0, result.Rank);
        Assert.Equal("DE", result.Label);
    }

    [Fact]
    public void Match_SecondaryLanguage_RankByPreferenceOrder()
    {
        var prefs = Prefs("de", new[] { "en", "es" }, false);

        var english = LanguageMatcher.Match(prefs, new[] { "English" });
        var spanish = LanguageMatcher.Match(prefs, new[] { "Spanish" });

        Assert.Equal(1, english.Rank);
        Assert.Equal(2, spanish.Rank);
    }

    [Fact]
    public void Match_NoMatch_DroppedWhenFiltering()
    {
        var prefs = Prefs("de", new[] { "en" }, true);
        var result = LanguageMatcher.Match(prefs, new[] { "French" });

        Assert.False(result.Keep);
    }

    [Fact]
    public void Match_NoMatch_KeptWhenNotFiltering()
    {
        var prefs = Prefs("de", new[] { "en" }, false);
        var result = LanguageMatcher.Match(prefs, new[] { "French" });

        Assert.True(result.Keep);
    }

    [Fact]
    public void Match_UnknownLanguage_AlwaysKept()
    {
        var prefs = Prefs("de", new[] { "en" }, true);
        var result = LanguageMatcher.Match(prefs, Array.Empty<string>());

        Assert.True(result.Keep);
    }
}
