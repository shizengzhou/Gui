using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Gui;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The localized strings. English is the source text and Chinese the translation, so both
/// languages must provide every string and stay in their own script.
/// </summary>
[Collection("Strings")]
public class StringsTests
{
    private static readonly Regex Chinese = new("[\u4e00-\u9fff]", RegexOptions.Compiled);

    private static PropertyInfo[] TextProperties => typeof(Strings)
        .GetProperties(BindingFlags.Public | BindingFlags.Static)
        .Where(property => property.PropertyType == typeof(string))
        .ToArray();

    // Program names and other literals read the same in both languages, so the script check
    // below skips them.
    private static readonly string[] Untranslated =
    {
        nameof(Strings.SettingsCommandAuto),
    };

    private static PropertyInfo[] TranslatedProperties => TextProperties
        .Where(property => !Untranslated.Contains(property.Name))
        .ToArray();

    [Fact]
    public void EveryStringIsProvidedInBothLanguages()
    {
        try
        {
            Strings.Use(Language.English);
            foreach (PropertyInfo property in TextProperties)
                Assert.False(string.IsNullOrEmpty((string?)property.GetValue(null)), "English: " + property.Name);

            Strings.Use(Language.Chinese);
            foreach (PropertyInfo property in TextProperties)
                Assert.False(string.IsNullOrEmpty((string?)property.GetValue(null)), "Chinese: " + property.Name);
        }
        finally
        {
            Strings.Use(Language.English);
        }
    }

    [Fact]
    public void EnglishTextContainsNoChineseAndTheTranslationDoes()
    {
        try
        {
            Strings.Use(Language.English);
            foreach (PropertyInfo property in TranslatedProperties)
                Assert.DoesNotMatch(Chinese, (string)property.GetValue(null)!);

            Strings.Use(Language.Chinese);
            foreach (PropertyInfo property in TranslatedProperties)
                Assert.Matches(Chinese, (string)property.GetValue(null)!);
        }
        finally
        {
            Strings.Use(Language.English);
        }
    }

    [Fact]
    public void FormattedStringsKeepTheirTitle()
    {
        try
        {
            Strings.Use(Language.English);
            Assert.Equal("Tab: shell", Strings.TabName("shell"));

            Strings.Use(Language.Chinese);
            Assert.Equal("标签页: shell", Strings.TabName("shell"));
        }
        finally
        {
            Strings.Use(Language.English);
        }
    }
}
