using System.Collections.Generic;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The locale given to DirectWrite decides which face its fallback hands back for CJK, so it
/// has to be the user's own CJK language rather than whatever the process locale is.
/// </summary>
public class FontLocaleTests
{
    [Fact]
    public void TheUsersCjkLanguageWinsOverAnEnglishUi()
    {
        // The machine that showed Gui drawing 门 the Japanese way.
        Assert.Equal("zh-Hans-CN", FontLocale.ForGlyphFallback(new[] { "en-US", "zh-Hans-CN" }, "en-US"));
    }

    [Fact]
    public void TheFirstCjkLanguageInTheListIsUsed()
    {
        Assert.Equal("ja-JP", FontLocale.ForGlyphFallback(new[] { "ja-JP", "zh-Hans-CN" }, "en-US"));
    }

    [Fact]
    public void WithoutACjkLanguageTheProcessLocaleStays()
    {
        Assert.Equal("en-US", FontLocale.ForGlyphFallback(new[] { "en-US", "de-DE" }, "en-US"));
        Assert.Equal("en-US", FontLocale.ForGlyphFallback(new List<string>(), "en-US"));
    }

    [Fact]
    public void ScriptOnlyTagsStillCount()
    {
        // "zh-Hans" carries no region; the primary subtag is what matters.
        Assert.Equal("zh-Hans", FontLocale.ForGlyphFallback(new[] { "zh-Hans" }, "en-US"));
    }

    [Fact]
    public void APrefixThatIsNotALanguageDoesNotCount()
    {
        // "kok" is Konkani, not Korean.
        Assert.Equal("en-US", FontLocale.ForGlyphFallback(new[] { "kok-IN", "zho" }, "en-US"));
    }
}
