using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The Ghostty configuration surface: every documented option must be recognized, and the
/// options this build honors must resolve into the typed values the app uses.
/// </summary>
[Collection("Strings")]
public class ConfigSurfaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gui-surface-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // A leftover temp directory must not fail the test run.
        }
    }

    private string Write(string content)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "config");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string Sample(ConfigKind kind) => kind switch
    {
        ConfigKind.Bool => "true",
        ConfigKind.Integer => "1",
        ConfigKind.Float => "0.5",
        ConfigKind.Number => "1",
        ConfigKind.Color => "#112233",
        ConfigKind.Enum => "auto",
        ConfigKind.List => "sample",
        _ => "sample",
    };

    [Fact]
    public void TheSchemaCoversEveryGhosttyReferenceOption()
    {
        Assert.Equal(213, ConfigSchema.Count);
        Assert.Equal(213, ConfigSchema.Ghostty.Select(o => o.Name).Distinct().Count());

        foreach (string name in new[]
                 {
                     "font-family", "font-size", "theme", "background", "palette", "cursor-style",
                     "window-padding-x", "keybind", "clipboard-read", "scrollback-limit-bytes",
                     "scrollback-compression", "link-osc8", "middle-click-action", "drag-handle",
                     "macos-window-shadow", "gtk-tabs-location", "auto-update-channel",
                 })
        {
            Assert.True(ConfigSchema.IsKnown(name), name);
        }
    }

    [Fact]
    public void EveryOptionParsesWithoutAWarning()
    {
        // background/foreground/cursor-color/palette resolve through the native palette
        // generator, which is covered by the tests that ship the app; here the goal is the
        // parser accepting every documented key.
        var skip = new HashSet<string> { "background", "foreground", "cursor-color", "palette" };

        var text = new StringBuilder();
        foreach (ConfigOption option in ConfigSchema.Ghostty)
        {
            if (skip.Contains(option.Name)) continue;
            text.Append(option.Name).Append(" = ").Append(Sample(option.Kind)).Append('\n');
        }

        Config config = Config.Load(Write(text.ToString()));

        ConfigDiagnostic[] warnings = config.Diagnostics
            .Where(d => d.Severity == ConfigSeverity.Warning)
            .ToArray();
        Assert.Empty(warnings);
    }

    [Fact]
    public void PlatformOnlyOptionsAreRecognizedWithANotice()
    {
        Config config = Config.Load(Write("macos-window-shadow = true\ngtk-tabs-location = bottom\n"));

        Assert.Contains(config.Diagnostics,
            d => d.Key == "macos-window-shadow" && d.Severity == ConfigSeverity.Notice);
        Assert.Contains(config.Diagnostics,
            d => d.Key == "gtk-tabs-location" && d.Severity == ConfigSeverity.Notice);
        Assert.Equal("bottom", config.Last("gtk-tabs-location"));
    }

    [Fact]
    public void UnknownOptionsAndInvalidValuesBecomeWarnings()
    {
        Config config = Config.Load(Write("no-such-option = 1\nfont-size = huge\n"));

        Assert.Contains(config.Diagnostics,
            d => d.Key == "no-such-option" && d.Severity == ConfigSeverity.Warning);
        Assert.Contains(config.Diagnostics,
            d => d.Key == "font-size" && d.Severity == ConfigSeverity.Warning);
        Assert.Equal(12f, config.FontSize);
    }

    [Fact]
    public void RepeatableOptionsAccumulateAndAnEmptyValueClearsThem()
    {
        Config config = Config.Load(Write(
            "font-family = Alpha\n" +
            "font-family = Beta\n" +
            "font-feature = -calt\n" +
            "font-feature = -liga\n"));

        Assert.Equal(2, config.All("font-family").Count);
        Assert.Equal("Beta", config.FontFamily);
        Assert.Equal(new[] { "-calt", "-liga" }, config.FontFeatures);

        Config cleared = Config.Load(Write("font-family = Alpha\nfont-family = \"\"\n"));
        Assert.Empty(cleared.All("font-family"));
        Assert.Equal(string.Empty, cleared.FontFamily);
    }

    [Fact]
    public void FontSizeIsInPointsAndConvertsToDeviceIndependentPixels()
    {
        // Ghostty, WezTerm and Windows Terminal all take points here, so the config value
        // is points and Win2D receives the DIP equivalent (1pt = 96/72 DIP).
        Config config = Config.Load(Write("font-size = 12\n"));

        Assert.Equal(12f, config.FontSize);
        Assert.Equal(16.0, config.FontSizeDip, 3);

        Assert.Equal(12f, Config.Load(Write(string.Empty)).FontSize);
    }

    [Fact]
    public void PixelAdjustmentsAreScaledByTheDisplayDpi()
    {
        // Ghostty's pixel amounts are physical pixels; the renderer measures in DIPs, so a
        // 2px adjustment is 2 DIP at 100% and 1.33 DIP at 150%.
        var twoPixels = new MetricAdjust(false, 2f);
        Assert.Equal(2f, twoPixels.Delta(16f, 1f));
        Assert.Equal(2f / 1.5f, twoPixels.Delta(16f, 1.5f), 3);
        Assert.Equal(11f, twoPixels.Apply(10f, 2f), 3);

        // Percentages are relative to the base value and therefore DPI independent.
        var tenPercent = new MetricAdjust(true, 10f);
        Assert.Equal(2f, tenPercent.Delta(20f, 1.5f), 3);
        Assert.Equal(22f, tenPercent.Apply(20f, 2f), 3);
    }

    [Fact]
    public void AdjustmentsAcceptPixelsAndPercentages()
    {
        Config config = Config.Load(Write(
            "adjust-cell-width = 2\n" +
            "adjust-cell-height = -10%\n" +
            "adjust-font-baseline = 1\n"));

        Assert.False(config.CellWidthAdjust.IsPercent);
        Assert.Equal(2f, config.CellWidthAdjust.Amount);
        Assert.Equal(12f, config.CellWidthAdjust.Apply(10f));

        Assert.True(config.CellHeightAdjust.IsPercent);
        Assert.Equal(18f, config.CellHeightAdjust.Apply(20f));
        Assert.Equal(-2f, config.CellHeightAdjust.Delta(20f));

        Assert.Equal(1f, config.FontBaselineAdjust.Delta(16f));
    }

    [Fact]
    public void ColorsAcceptHexFormsAndNames()
    {
        Assert.True(Config.TryParseColor("#abc", out Rgb shortHex));
        Assert.Equal(new Rgb(0xAA, 0xBB, 0xCC), shortHex);

        Assert.True(Config.TryParseColor("#112233", out Rgb hex));
        Assert.Equal(new Rgb(0x11, 0x22, 0x33), hex);

        Assert.True(Config.TryParseColor("red", out Rgb red));
        Assert.Equal(new Rgb(255, 0, 0), red);

        Assert.False(Config.TryParseColor("not-a-color", out _));
    }

    [Fact]
    public void CursorAndSelectionColorsResolve()
    {
        Config config = Config.Load(Write(
            "cursor-text = #010203\n" +
            "cursor-opacity = 0.5\n" +
            "cursor-style = bar\n" +
            "cursor-style-blink = false\n" +
            "selection-background = blue\n" +
            "selection-foreground = #445566\n" +
            "bold-color = #AABBCC\n" +
            "faint-opacity = 0.4\n"));

        Assert.True(config.HasCursorText);
        Assert.Equal(new Rgb(1, 2, 3), config.CursorText);
        Assert.Equal(0.5f, config.CursorOpacity);
        Assert.Equal("bar", config.CursorStyle);
        Assert.False(config.CursorBlink);

        Assert.True(config.HasSelectionBackground);
        Assert.Equal(new Rgb(0, 0, 255), config.SelectionBackground);

        Assert.True(config.HasSelectionForeground);
        Assert.Equal(new Rgb(0x44, 0x55, 0x66), config.SelectionForeground);

        Assert.True(config.HasBoldColor);
        Assert.Equal(new Rgb(0xAA, 0xBB, 0xCC), config.BoldColor);
        Assert.Equal(0.4f, config.FaintOpacity);
    }

    [Fact]
    public void ScrollMultiplierPrefersTheDiscreteEntry()
    {
        Assert.Equal(3f, Config.Load(Write("mouse-scroll-multiplier = precision:0.1,discrete:3\n")).MouseScrollMultiplier);
        Assert.Equal(2f, Config.Load(Write("mouse-scroll-multiplier = 2\n")).MouseScrollMultiplier);
        Assert.Equal(3f, Config.Load(Write("mouse-scroll-multiplier = [bad]\n")).MouseScrollMultiplier);
        Assert.Equal(3f, new Config().MouseScrollMultiplier);
    }

    [Fact]
    public void ProcessOptionsResolve()
    {
        Config config = Config.Load(Write(
            "env = FOO=bar\n" +
            "env = BAZ=qux=1\n" +
            "input = echo hi\n" +
            "input = \\r\n" +
            "term = xterm-256color\n" +
            "wait-after-command = true\n" +
            "initial-command = cmd.exe\n"));

        Assert.Equal(2, config.ExtraEnvironment.Count);
        Assert.Equal(("FOO", "bar"), config.ExtraEnvironment[0]);
        Assert.Equal(("BAZ", "qux=1"), config.ExtraEnvironment[1]);
        Assert.Equal("echo hi\\r", config.Input);
        Assert.Equal("xterm-256color", config.TermVariable);
        Assert.True(config.WaitAfterCommand);
        Assert.Equal("cmd.exe", config.InitialCommand);
    }

    [Fact]
    public void ClipboardAndMouseOptionsResolve()
    {
        Config config = Config.Load(Write(
            "copy-on-select = both\n" +
            "right-click-action = ignore\n" +
            "clipboard-trim-trailing-spaces = false\n" +
            "selection-clear-on-copy = true\n" +
            "window-padding-x = 8,6\n" +
            "window-padding-y = 4\n"));

        Assert.True(config.CopyOnSelect);
        Assert.Equal("ignore", config.RightClickAction);
        Assert.False(config.ClipboardTrimTrailingSpaces);
        Assert.True(config.SelectionClearOnCopy);
        Assert.Equal(8, config.PaddingLeft);
        Assert.Equal(6, config.PaddingRight);
        Assert.Equal(4, config.PaddingTop);
        Assert.Equal(4, config.PaddingBottom);

        // Unset keeps Ghostty's defaults: trim on copy, context menu, 2 DIP padding.
        Config defaults = Config.Load(Write(string.Empty));
        Assert.True(defaults.ClipboardTrimTrailingSpaces);
        Assert.Equal("context-menu", defaults.RightClickAction);

        Config fallback = Config.Load(Write("right-click-action = nothing\n"));
        Assert.Equal("context-menu", fallback.RightClickAction);
    }

    [Fact]
    public void TheSchemaHasNoDuplicateOrShadowedNames()
    {
        string[] ghostty = ConfigSchema.Ghostty.Select(o => o.Name).ToArray();
        Assert.Equal(ghostty.Length, ghostty.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        string[] application = ConfigSchema.Application.Select(o => o.Name).ToArray();
        Assert.Equal(application.Length, application.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(application.Intersect(ghostty, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AliasesResolveToTheCanonicalOption()
    {
        Assert.True(ConfigSchema.TryGet("shell", out ConfigOption shell));
        Assert.Equal("command", shell.Name);

        Assert.True(ConfigSchema.TryGet("cwd", out ConfigOption cwd));
        Assert.Equal("working-directory", cwd.Name);
    }

    /// <summary>
    /// The checklist is the contract for "all Ghostty options": every schema option must be
    /// listed, nothing unknown may appear, and no key may be listed twice. The checklist also
    /// documents a few app-only options, so the counts need not match exactly.
    /// </summary>
    [Fact]
    public void TheChecklistListsExactlyTheSchemaOptions()
    {
        string[] documented = File.ReadAllLines(Path.Combine(RepositoryRoot(), "GHOSTTY-CONFIG-TODO.md"))
            .Select(line => Regex.Match(line, "^- \\[[ x]\\] `([^`]+)`"))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .ToArray();

        string[] schema = ConfigSchema.Ghostty.Select(option => option.Name).ToArray();

        Assert.Empty(schema.Except(documented, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(documented.Length, documented.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (string name in documented) Assert.True(ConfigSchema.IsKnown(name), name);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Gui.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [Fact]
    public void LanguageFollowsTheSystemUnlessOverridden()
    {
        // With no explicit value the detected system language is used.
        Assert.Equal(Config.DetectSystemLanguage(), Config.Load(Write(string.Empty)).Language);

        Assert.Equal(Language.Chinese, Config.Load(Write("language = zh-CN\n")).Language);
        Assert.Equal(Language.Chinese, Config.Load(Write("language = zh\n")).Language);
        Assert.Equal(Language.Chinese, Config.Load(Write("language = chinese\n")).Language);
        Assert.Equal(Language.Chinese, Config.Load(Write("language = zh-Hans\n")).Language);

        // Any other tag keeps English, even when the system language is Chinese.
        Assert.Equal(Language.English, Config.Load(Write("language = de\n")).Language);
        Assert.Equal(Language.English, Config.Load(Write("language = en-GB\n")).Language);
    }

    [Fact]
    public void SimplifiedChineseDetectionExcludesTraditionalVariants()
    {
        Assert.True(Config.IsSimplifiedChinese("zh"));
        Assert.True(Config.IsSimplifiedChinese("zh-CN"));
        Assert.True(Config.IsSimplifiedChinese("zh-SG"));
        Assert.True(Config.IsSimplifiedChinese("zh-Hans"));
        Assert.True(Config.IsSimplifiedChinese("zh-Hans-CN"));

        Assert.False(Config.IsSimplifiedChinese("zh-TW"));
        Assert.False(Config.IsSimplifiedChinese("zh-HK"));
        Assert.False(Config.IsSimplifiedChinese("zh-MO"));
        Assert.False(Config.IsSimplifiedChinese("zh-Hant"));
        Assert.False(Config.IsSimplifiedChinese("zh-Hant-TW"));

        Assert.False(Config.IsSimplifiedChinese("en-US"));
        Assert.False(Config.IsSimplifiedChinese(null));
        Assert.False(Config.IsSimplifiedChinese(string.Empty));
    }

    [Fact]
    public void StringsFollowTheSelectedLanguage()
    {
        try
        {
            Strings.Use(Language.English);
            Assert.Equal("Split right", Strings.SplitRight);
            Assert.Equal("Tab: x", Strings.TabName("x"));

            Strings.Use(Language.Chinese);
            Assert.Equal("向右拆分窗格", Strings.SplitRight);
            Assert.Equal("标签页: x", Strings.TabName("x"));
        }
        finally
        {
            Strings.Use(Language.English);
        }
    }

    [Fact]
    public void FontStyleNamesMapToWeightAndSlant()
    {
        Assert.Equal(new FaceStyle(false, 400, false), FontStyleNames.Parse(null, false, false));
        Assert.Equal(new FaceStyle(false, 700, false), FontStyleNames.Parse(null, true, false));
        Assert.Equal(new FaceStyle(false, 400, true), FontStyleNames.Parse(null, false, true));

        Assert.Equal(900, FontStyleNames.Parse("Heavy", false, false).Weight);
        Assert.Equal(300, FontStyleNames.Parse("Light", false, false).Weight);

        FaceStyle semiBoldItalic = FontStyleNames.Parse("SemiBold Italic", false, false);
        Assert.Equal(600, semiBoldItalic.Weight);
        Assert.True(semiBoldItalic.Italic);

        Assert.True(FontStyleNames.Parse("false", true, false).Disabled);
        Assert.Equal(new FaceStyle(false, 700, false), FontStyleNames.Parse("UnknownStyle", true, false));

        // A bare weight number, as kero and Windows Terminal take it.
        Assert.Equal(new FaceStyle(false, 600, false), FontStyleNames.Parse("600", false, false));
        Assert.Equal(new FaceStyle(false, 350, true), FontStyleNames.Parse("350", false, true));
    }

    [Fact]
    public void ClipboardCodepointMapRewritesCopiedText()
    {
        Config maps = Config.Load(Write(
            "clipboard-codepoint-map = U+2500=U+002D\n" +
            "clipboard-codepoint-map = U+2502=U+007C\n" +
            "clipboard-codepoint-map = U+03A3=SUM\n"));

        Assert.Equal(3, maps.ClipboardCodepointMap.Count);
        Assert.Equal("-|SUM", maps.ApplyClipboardCodepointMap("\u2500\u2502\u03A3"));

        Config range = Config.Load(Write("clipboard-codepoint-map = U+2500-U+2502=#\n"));
        Assert.Equal("##", range.ApplyClipboardCodepointMap("\u2500\u2502"));

        // Later entries win for overlapping ranges.
        Config later = Config.Load(Write(
            "clipboard-codepoint-map = U+2500=U+002D\n" +
            "clipboard-codepoint-map = U+2500=U+005F\n"));
        Assert.Equal("_", later.ApplyClipboardCodepointMap("\u2500"));
    }

    [Fact]
    public void FontCodepointMapResolvesRangesAndFamilies()
    {
        Config config = Config.Load(Write(
            "font-codepoint-map = U+E000-U+F8FF=Symbols Nerd Font\n" +
            "font-codepoint-map = U+2500=Consolas\n" +
            "font-codepoint-map = no-separator\n"));

        Assert.Equal(2, config.FontCodepointMap.Count);
        Assert.Equal(new FontCodepointMap(0xE000, 0xF8FF, "Symbols Nerd Font"), config.FontCodepointMap[0]);
        Assert.Equal(new FontCodepointMap(0x2500, 0x2500, "Consolas"), config.FontCodepointMap[1]);

        // The reverse order and lower-case prefixes parse the same way.
        Config reversed = Config.Load(Write("font-codepoint-map = 0xF8FF-0xE000=Mono\n"));
        Assert.Equal(new FontCodepointMap(0xE000, 0xF8FF, "Mono"), reversed.FontCodepointMap[0]);
    }

    [Fact]
    public void IconHeightAndShapingBreakResolve()
    {
        Config config = Config.Load(Write(
            "adjust-icon-height = 2\n" +
            "font-shaping-break = cursor\n"));

        Assert.False(config.IconHeightAdjust.IsPercent);
        Assert.Equal(2f, config.IconHeightAdjust.Amount);

        // Ghostty breaks shaping at the cursor unless "no-cursor" is given.
        Assert.True(config.FontShapingBreak);
        Assert.True(new Config().FontShapingBreak);
        Assert.False(Config.Load(Write("font-shaping-break = no-cursor\n")).FontShapingBreak);

        Assert.True(Config.Load(Write("adjust-icon-height = 10%\n")).IconHeightAdjust.IsPercent);
    }

    [Fact]
    public void FontVariationAxesParsePerFace()
    {
        Config config = Config.Load(Write(
            "font-variation = wght=450\n" +
            "font-variation = opsz=13, GRAD=50\n" +
            "font-variation-bold = wght=700\n" +
            "font-variation-italic = ital=1, slnt=-10\n" +
            "font-variation-bold-italic = wght=900,wdth=75\n"));

        // Regular: repeated lines accumulate, commas split one line, axis names lower-case.
        Assert.Equal(3, config.FontVariations.Count);
        Assert.Equal(new FontVariation("wght", 450f), config.FontVariations[0]);
        Assert.Equal(new FontVariation("opsz", 13f), config.FontVariations[1]);
        Assert.Equal(new FontVariation("grad", 50f), config.FontVariations[2]);

        Assert.Equal(new FontVariation("wght", 700f), Assert.Single(config.FontVariationsBold));

        Assert.Equal(2, config.FontVariationsItalic.Count);
        Assert.Equal(new FontVariation("ital", 1f), config.FontVariationsItalic[0]);
        Assert.Equal(new FontVariation("slnt", -10f), config.FontVariationsItalic[1]);

        Assert.Equal(2, config.FontVariationsBoldItalic.Count);
        Assert.Equal(new FontVariation("wght", 900f), config.FontVariationsBoldItalic[0]);
        Assert.Equal(new FontVariation("wdth", 75f), config.FontVariationsBoldItalic[1]);
    }

    [Fact]
    public void MalformedFontVariationEntriesAreIgnored()
    {
        Config config = Config.Load(Write(
            "font-variation = wght\n" +          // no '='
            "font-variation = =600\n" +          // empty axis
            "font-variation = wght=heavy\n" +    // value is not a number
            "font-variation = wght=600,\n" +     // trailing empty part is dropped
            "font-variation = WGHT=600\n"));     // valid, normalized to lower case

        // Only the two well-formed entries survive.
        Assert.Equal(2, config.FontVariations.Count);
        Assert.All(config.FontVariations, variation =>
        {
            Assert.Equal("wght", variation.Axis);
            Assert.Equal(600f, variation.Value);
        });

        Assert.Empty(Config.Load(Write(string.Empty)).FontVariations);
    }

    [Fact]
    public void FontVariationsInheritTheRegularAxesPerFace()
    {
        Config config = Config.Load(Write(
            "font-variation = wght=450\n" +
            "font-variation-italic = ital=1\n"));

        var regular = new FontVariation("wght", 450f);
        Assert.Equal(regular, Assert.Single(config.VariationsFor(false, false)));
        Assert.Equal(regular, Assert.Single(config.VariationsFor(true, false)));
        Assert.Equal(regular, Assert.Single(config.VariationsFor(true, true)));

        // A face with its own axes does not inherit.
        Assert.Equal(new FontVariation("ital", 1f), Assert.Single(config.VariationsFor(false, true)));

        Assert.Empty(new Config().VariationsFor(true, true));
    }

    [Fact]
    public void SyntheticStyleTokensToggleTheirOwnStyle()
    {
        // Each token only affects the style it names.
        Config noBold = Config.Load(Write("font-synthetic-style = no-bold\n"));
        Assert.False(noBold.SyntheticBold);
        Assert.True(noBold.SyntheticItalic);
        Assert.True(noBold.SyntheticBoldItalic);

        Config noItalic = Config.Load(Write("font-synthetic-style = no-italic\n"));
        Assert.True(noItalic.SyntheticBold);
        Assert.False(noItalic.SyntheticItalic);
        Assert.True(noItalic.SyntheticBoldItalic);

        Config noBoldItalic = Config.Load(Write("font-synthetic-style = no-bold-italic\n"));
        Assert.True(noBoldItalic.SyntheticBold);
        Assert.True(noBoldItalic.SyntheticItalic);
        Assert.False(noBoldItalic.SyntheticBoldItalic);

        // Tokens are case-insensitive and one line may hold several of them.
        Config mixed = Config.Load(Write("font-synthetic-style = NO-BOLD, no-italic\n"));
        Assert.False(mixed.SyntheticBold);
        Assert.False(mixed.SyntheticItalic);
        Assert.True(mixed.SyntheticBoldItalic);

        // Unknown tokens are ignored instead of being treated as a style.
        Config unknown = Config.Load(Write("font-synthetic-style = no-bold, wobble\n"));
        Assert.False(unknown.SyntheticBold);
        Assert.True(unknown.SyntheticItalic);
    }

    [Fact]
    public void SyntheticStyleLaterTokensWinAndClearingRestoresDefaults()
    {
        // Tokens apply in order, so the last mention of a style decides.
        Config combined = Config.Load(Write(
            "font-synthetic-style = no-bold, bold\n" +
            "font-synthetic-style = no-italic\n"));
        Assert.True(combined.SyntheticBold);
        Assert.False(combined.SyntheticItalic);
        Assert.True(combined.SyntheticBoldItalic);

        Config reversed = Config.Load(Write("font-synthetic-style = bold, no-bold\n"));
        Assert.False(reversed.SyntheticBold);

        // An empty value clears the list, which brings every default back.
        Config cleared = Config.Load(Write(
            "font-synthetic-style = no-bold\n" +
            "font-synthetic-style = \"\"\n"));
        Assert.True(cleared.SyntheticBold);
        Assert.True(cleared.SyntheticItalic);
        Assert.True(cleared.SyntheticBoldItalic);

        // Everything is synthesized by default, exactly like Ghostty.
        var defaults = new Config();
        Assert.True(defaults.SyntheticBold);
        Assert.True(defaults.SyntheticItalic);
        Assert.True(defaults.SyntheticBoldItalic);
    }

    [Fact]
    public void BackgroundImageOptionsResolve()
    {
        Config config = Config.Load(Write(
            "background-image = bg.png\n" +
            "background-opacity = 0.75\n" +
            "background-image-opacity = 0.4\n" +
            "background-image-position = bottom-right\n" +
            "background-image-fit = cover\n" +
            "background-image-repeat = true\n"));

        // A relative path resolves against the config file's directory.
        Assert.Equal(Path.Combine(_dir, "bg.png"), config.BackgroundImage);
        Assert.Equal(0.75f, config.BackgroundOpacity);
        Assert.Equal(0.4f, config.BackgroundImageOpacity);
        Assert.Equal("bottom-right", config.BackgroundImagePosition);
        Assert.Equal("cover", config.BackgroundImageFit);
        Assert.True(config.BackgroundImageRepeat);

        // Out-of-range values clamp; unknown names fall back to Ghostty's defaults.
        Config fallback = Config.Load(Write(
            "background-opacity = 2\n" +
            "background-image-opacity = 2\n" +
            "background-image-position = sideways\n" +
            "background-image-fit = squish\n"));
        Assert.Equal(1f, fallback.BackgroundOpacity);
        Assert.Equal(1f, fallback.BackgroundImageOpacity);
        Assert.Equal("center", fallback.BackgroundImagePosition);
        Assert.Equal("contain", fallback.BackgroundImageFit);
        Assert.False(fallback.BackgroundImageRepeat);
        Assert.Equal(string.Empty, fallback.BackgroundImage);

        // A negative value clamps to fully transparent, not below zero.
        Assert.Equal(0f, Config.Load(Write("background-opacity = -1\n")).BackgroundOpacity);

        // "~" expands to the user profile.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "bg.png"),
            Config.Load(Write("background-image = ~/bg.png\n")).BackgroundImage);
    }

    [Fact]
    public void BackgroundOpacityCellsDefaultsToOpaqueCellBackgrounds()
    {
        // Ghostty keeps explicit cell backgrounds opaque unless the option opts in.
        Assert.False(new Config().BackgroundOpacityCells);
        Assert.False(Config.Load(Write(string.Empty)).BackgroundOpacityCells);
        Assert.True(Config.Load(Write("background-opacity-cells = true\n")).BackgroundOpacityCells);
        Assert.False(Config.Load(Write("background-opacity-cells = false\n")).BackgroundOpacityCells);
    }

    [Fact]
    public void BackgroundBlurAcceptsBooleansAndRadii()
    {
        // Booleans are case-insensitive.
        Assert.True(Config.Load(Write("background-blur = true\n")).BackgroundBlur);
        Assert.True(Config.Load(Write("background-blur = ON\n")).BackgroundBlur);
        Assert.True(Config.Load(Write("background-blur = yes\n")).BackgroundBlur);
        Assert.False(Config.Load(Write("background-blur = false\n")).BackgroundBlur);
        Assert.False(Config.Load(Write("background-blur = Off\n")).BackgroundBlur);
        Assert.False(Config.Load(Write("background-blur = no\n")).BackgroundBlur);

        // A positive radius enables blur; zero and negatives disable it.
        Assert.True(Config.Load(Write("background-blur = 20\n")).BackgroundBlur);
        Assert.True(Config.Load(Write("background-blur = 0.5\n")).BackgroundBlur);
        Assert.False(Config.Load(Write("background-blur = 0\n")).BackgroundBlur);
        Assert.False(Config.Load(Write("background-blur = -2\n")).BackgroundBlur);

        // Unset keeps Ghostty's default (off); a macOS glass value implies on.
        Assert.False(new Config().BackgroundBlur);
        Assert.False(Config.Load(Write(string.Empty)).BackgroundBlur);
        Assert.True(Config.Load(Write("background-blur = macos-glass-regular\n")).BackgroundBlur);
    }

    [Fact]
    public void SelectionWordCharsResolve()
    {
        // Ghostty's default boundary set: tab, quotes, │, backtick, |, :;, ()[]{}<>, $.
        Assert.Equal("\t'\"│`|:;,()[]{}<>$", Config.DefaultSelectionWordChars);
        Assert.Equal(Config.DefaultSelectionWordChars, new Config().SelectionWordChars);
        Assert.Equal(Config.DefaultSelectionWordChars, Config.Load(Write(string.Empty)).SelectionWordChars);

        Assert.Equal(",;:", Config.Load(Write("selection-word-chars = ,;:\n")).SelectionWordChars);
    }

    [Fact]
    public void OverlineAdjustmentsResolve()
    {
        Config config = Config.Load(Write(
            "adjust-overline-position = 2\n" +
            "adjust-overline-thickness = 20%\n"));

        Assert.Equal(2f, config.OverlinePositionAdjust.Amount);
        Assert.False(config.OverlinePositionAdjust.IsPercent);
        Assert.Equal(20f, config.OverlineThicknessAdjust.Amount);
        Assert.True(config.OverlineThicknessAdjust.IsPercent);
    }

    [Fact]
    public void CopyFromCarriesTheExtendedOptions()
    {
        Config source = Config.Load(Write(
            "language = zh-CN\n" +
            "font-family-bold = Bold Mono\n" +
            "font-codepoint-map = U+2500=Consolas\n" +
            "background-image = C:\\images\\bg.png\n" +
            "background-image-fit = cover\n" +
            "background-opacity = 0.8\n" +
            "background-opacity-cells = true\n" +
            "background-blur = false\n" +
            "font-variation = wght=600\n" +
            "font-synthetic-style = no-bold\n" +
            "adjust-cell-width = 1\n" +
            "adjust-icon-height = 1\n" +
            "font-shaping-break = no-cursor\n" +
            "selection-word-chars = ,;\n" +
            "cursor-style = underline\n" +
            "env = A=1\n" +
            "mouse-scroll-multiplier = 4\n" +
            "window-padding-x = 6\n"));

        var target = new Config();
        target.CopyFrom(source);

        Assert.Equal(Language.Chinese, target.Language);
        Assert.Equal("Bold Mono", target.FontFamilyBold);
        Assert.Equal(new FontCodepointMap(0x2500, 0x2500, "Consolas"), target.FontCodepointMap[0]);
        Assert.Equal("C:\\images\\bg.png", target.BackgroundImage);
        Assert.Equal("cover", target.BackgroundImageFit);
        Assert.Equal(0.8f, target.BackgroundOpacity);
        Assert.True(target.BackgroundOpacityCells);
        Assert.False(target.BackgroundBlur);
        Assert.Equal(new FontVariation("wght", 600f), target.FontVariations[0]);
        Assert.False(target.SyntheticBold);
        Assert.Equal(1f, target.IconHeightAdjust.Amount);
        Assert.False(target.FontShapingBreak);
        Assert.Equal(",;", target.SelectionWordChars);
        Assert.Equal(1f, target.CellWidthAdjust.Amount);
        Assert.Equal("underline", target.CursorStyle);
        Assert.Equal(("A", "1"), target.ExtraEnvironment[0]);
        Assert.Equal(4f, target.MouseScrollMultiplier);
        Assert.Equal(6, target.PaddingLeft);
        Assert.Equal(6, target.PaddingRight);
        Assert.Equal(source.All("env"), target.All("env"));
    }
}
