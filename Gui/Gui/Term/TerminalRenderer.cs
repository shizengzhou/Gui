using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI;

namespace Gui.Term;

/// <summary>
/// Win2D renderer for a libghostty-vt render state. No terminal logic lives here.
/// </summary>
public sealed unsafe class TerminalRenderer
{
    private const uint FR_PRIVATE = 0x10;

    private static readonly string[] BundledFontFiles =
    {
        "JetBrainsMonoNerdFont-Regular.ttf",
        "JetBrainsMonoNerdFont-Bold.ttf",
        "JetBrainsMonoNerdFont-Italic.ttf",
        "JetBrainsMonoNerdFont-BoldItalic.ttf",
    };

    private readonly Dictionary<int, CanvasTextFormat> _formats = new();
    private readonly StringBuilder _text = new(64);

    /// <summary>Cached text layouts, keyed by the run text and the face it is drawn with.</summary>
    private readonly Dictionary<LayoutKey, CanvasTextLayout> _layouts = new();

    /// <summary>
    /// Typography for "font-feature"; null when no feature this build understands is set.
    /// </summary>
    private CanvasTypography? _typography;

    /// <summary>An unbounded cache would grow for the lifetime of the window, so it is capped.</summary>
    private const int LayoutCacheCapacity = 1024;

    /// <summary>System font faces, consulted only when synthetic styles are disabled.</summary>
    private CanvasFontSet? _systemFontSet;
    private readonly Dictionary<(string Family, ushort Weight, bool Italic), bool> _realFaces = new();

    /// <summary>"background-image": decoded once and redrawn every frame.</summary>
    private CanvasBitmap? _backgroundImage;
    private string _backgroundImagePath = string.Empty;

    /// <summary>Raised when an asynchronously loaded background image becomes available.</summary>
    public event Action? NeedsRedraw;

    private readonly record struct LayoutKey(string Text, int Face);

    private CanvasTextFormat _baseFormat = CreateFormat("Consolas", 13f, false, 400);
    private string _fontFamily = "JetBrainsMono Nerd Font";
    private string _fontFamilyBold = string.Empty;
    private string _fontFamilyItalic = string.Empty;
    private string _fontFamilyBoldItalic = string.Empty;
    private FaceStyle[] _faceStyles =
    {
        new(false, 400, false),
        new(false, 700, false),
        new(false, 400, true),
        new(false, 700, true),
    };
    private float _fontSize = 13f;
    private float _rasterizationScale = 1f;
    private float _textOffsetY;
    private Config? _config;
    private CellMetrics _metrics = new(8, 16);
    private ushort _cursorX;
    private ushort _cursorY;
    private bool _cursorValid;
    private string _imeComposition = string.Empty;

    private static bool _fontsRegistered;

    public TerminalRenderer()
    {
        RegisterBundledFonts();
        _baseFormat = CreateFormat(_fontFamily, _fontSize, false, 400);
    }

    public CellMetrics CellMetrics => _metrics;

    /// <summary>
    /// When true, text runs are drawn through a cached <see cref="CanvasTextLayout"/> so the
    /// OpenType, codepoint-map and icon-height options apply. When false the original
    /// <c>DrawText</c> path is used unchanged, which keeps a comparison/fallback available.
    /// </summary>
    internal bool UseTextLayout { get; set; } = true;

    /// <summary>
    /// The string the IME is composing right now (the pinyin before a candidate is picked).
    /// The terminal buffer knows nothing about it, so it is painted over the cells at the
    /// cursor and disappears when the composition ends.
    /// </summary>
    public bool SetImeComposition(string text)
    {
        if (_imeComposition == text) return false;
        _imeComposition = text;
        return true;
    }

    public bool CursorCell(out ushort x, out ushort y)
    {
        x = _cursorX;
        y = _cursorY;
        return _cursorValid;
    }

    /// <summary>
    /// Loads the TTFs shipped in Assets\Fonts for this process only, so DirectWrite's
    /// system collection can resolve the family even when it is not installed.
    /// </summary>
    private static void RegisterBundledFonts()
    {
        if (_fontsRegistered) return;
        _fontsRegistered = true;

        string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        foreach (string name in BundledFontFiles)
        {
            string file = Path.Combine(directory, name);
            if (!File.Exists(file)) continue;
            AddFontResourceEx(file, FR_PRIVATE, IntPtr.Zero);
        }
    }

    private static CanvasTextFormat CreateFormat(string family, float size, bool italic, ushort weight,
        Windows.UI.Text.FontStretch stretch = Windows.UI.Text.FontStretch.Normal)
    {
        return new CanvasTextFormat
        {
            FontFamily = family,
            FontSize = size,
            WordWrapping = CanvasWordWrapping.NoWrap,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = weight },
            FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            FontStretch = stretch,
        };
    }

    /// <summary>
    /// Applies the typography and layout options that affect measurement. Colors are read
    /// during <see cref="Render"/>, so they need no separate pass.
    /// </summary>
    public void ApplyConfig(Config config)
    {
        _config = config;
        _fontFamilyBold = config.FontFamilyBold;
        _fontFamilyItalic = config.FontFamilyItalic;
        _fontFamilyBoldItalic = config.FontFamilyBoldItalic;

        // font-style names become a DirectWrite weight/slant per face; "false" disables the
        // style, in which case the regular face is used.
        _faceStyles = new[]
        {
            FontStyleNames.Parse(config.FontStyleName, false, false),
            FontStyleNames.Parse(config.FontStyleBold, true, false),
            FontStyleNames.Parse(config.FontStyleItalic, false, true),
            FontStyleNames.Parse(config.FontStyleBoldItalic, true, true),
        };

        string family = string.IsNullOrEmpty(config.FontFamily) ? "JetBrainsMono Nerd Font" : config.FontFamily;
        float size = config.FontSizeDip;
        _fontFamily = family;
        _fontSize = size;
        BuildTypography();
        SetBackgroundImage(config.BackgroundImage);
        Rebuild(0);
    }

    /// <summary>
    /// Monospace family; bundled JetBrainsMono Nerd Font, then system fallbacks when the
    /// requested family is missing. The advance check rejects proportional fallbacks.
    /// </summary>
    public void SetFontFamily(string family)
    {
        // An empty value (config key removed) restores the bundled default.
        if (string.IsNullOrEmpty(family)) family = "JetBrainsMono Nerd Font";
        if (family == _fontFamily) return;
        _fontFamily = family;
        Rebuild(0);
    }

    /// <summary>Font size in DIPs. Recomputes cell metrics.</summary>
    public void SetFontSize(float size)
    {
        if (size < 6f || size > 96f) return;
        if (Math.Abs(size - _fontSize) < 0.01f) return;
        _fontSize = size;
        Rebuild(0);
    }

    /// <summary>
    /// Display scale, used to turn the "adjust-*" pixel amounts into DIPs. Ghostty's pixel
    /// values are physical pixels; this renderer measures in DIPs, so 2px at 150% is 1.33 DIP.
    /// </summary>
    public void SetRasterizationScale(double scale)
    {
        float value = scale > 0 ? (float)scale : 1f;
        if (Math.Abs(value - _rasterizationScale) < 0.001f) return;
        _rasterizationScale = value;
        Rebuild(0);
    }

    private void Rebuild(int attempt)
    {
        // Layouts bake in the face, size and per-range overrides, so any metric change
        // invalidates all of them.
        InvalidateLayouts();

        string[] candidates =
        {
            attempt == 0 ? _fontFamily : string.Empty,
            "JetBrainsMono Nerd Font",
            "Cascadia Mono",
            "Cascadia Code",
            "Consolas",
            "Courier New",
        };

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var probe = CreateFormat(candidate, _fontSize, false, 400);
            if (!IsMonospace(probe)) continue;
            _baseFormat = CreateBaseFormat(candidate);
            _formats.Clear();
            MeasureCell();
            return;
        }

        _baseFormat = CreateBaseFormat("Consolas");
        _formats.Clear();
        MeasureCell();
    }

    /// <summary>The regular face for a family, honoring font-style, variations and synthesis.</summary>
    private CanvasTextFormat CreateBaseFormat(string family)
    {
        (ushort weight, bool italic, Windows.UI.Text.FontStretch stretch) = ResolveFace(0, family);
        return CreateFormat(family, _fontSize, italic, weight, stretch);
    }

    private static bool IsMonospace(CanvasTextFormat format)
    {
        using var wide = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), "MMMMMMMMMM", format, 0, 0);
        using var narrow = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), "iiiiiiiiii", format, 0, 0);
        double wideAdvance = wide.LayoutBounds.Width;
        double narrowAdvance = narrow.LayoutBounds.Width;
        if (wideAdvance <= 0 || narrowAdvance <= 0) return false;
        return Math.Abs(wideAdvance - narrowAdvance) < 0.05;
    }

    private void MeasureCell()
    {
        using var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), "MMMM", _baseFormat, 0, 0);
        double advance = layout.LayoutBounds.Width / 4.0;
        if (advance < 1.0) advance = _fontSize * 0.6;
        double height = layout.LayoutBounds.Height;
        if (height < 1.0) height = _fontSize * 1.2;

        if (_config is not null)
        {
            advance = _config.CellWidthAdjust.Apply((float)advance, _rasterizationScale);
            height = _config.CellHeightAdjust.Apply((float)height, _rasterizationScale);
        }

        _metrics = new CellMetrics(
            (uint)Math.Max(1, Math.Ceiling(advance)),
            (uint)Math.Max(1, Math.Ceiling(height)));

        // Center the glyph box in the cell, then apply the baseline option (a positive
        // adjustment moves the baseline up, so it subtracts from the top offset).
        float offset = (float)((_metrics.Height - layout.LayoutBounds.Height) / 2.0);
        if (_config is not null) offset -= _config.FontBaselineAdjust.Delta(_metrics.Height, _rasterizationScale);
        _textOffsetY = offset;
    }

    private CanvasTextFormat FormatFor(bool bold, bool italic)
    {
        int key = (bold ? 1 : 0) | (italic ? 2 : 0);
        if (_formats.TryGetValue(key, out CanvasTextFormat? cached)) return cached;
        if (key == 0) return _baseFormat;

        string family = FamilyFor(key);
        (ushort weight, bool slant, Windows.UI.Text.FontStretch stretch) = ResolveFace(key, family);
        var format = CreateFormat(family, _fontSize, slant, weight, stretch);
        _formats[key] = format;
        return format;
    }

    private string FamilyFor(int key) => key switch
    {
        1 => UseOr(_fontFamilyBold, _fontFamily),
        2 => UseOr(_fontFamilyItalic, _fontFamily),
        3 => UseOr(_fontFamilyBoldItalic, UseOr(_fontFamilyBold, _fontFamily)),
        _ => _fontFamily,
    };

    /// <summary>
    /// Resolves a face to the weight, slant and width DirectWrite takes. The "font-style"
    /// name supplies the base face, then the "font-variation" axes this build can express
    /// (wght, wdth, ital, slnt) override it, and finally "font-synthetic-style" decides
    /// whether a missing bold/italic face may be faked.
    /// </summary>
    private (ushort Weight, bool Italic, Windows.UI.Text.FontStretch Stretch) ResolveFace(int key, string family)
    {
        FaceStyle style = _faceStyles[key];
        ushort weight = style.Disabled ? (ushort)400 : style.Weight;
        bool italic = !style.Disabled && style.Italic;
        var stretch = Windows.UI.Text.FontStretch.Normal;

        Config? config = _config;
        if (config is null || style.Disabled) return (weight, italic, stretch);

        List<FontVariation>? variations = VariationsFor(key);
        bool weightFromVariation = false;
        bool slantFromVariation = false;
        if (variations is not null)
        {
            bool slantSet = false;
            foreach (FontVariation variation in variations)
            {
                switch (variation.Axis)
                {
                    case "wght":
                        weight = (ushort)Math.Clamp(MathF.Round(variation.Value), 1f, 999f);
                        weightFromVariation = true;
                        break;
                    case "wdth":
                        stretch = StretchForWidth(variation.Value);
                        break;
                    case "ital":
                        italic = variation.Value >= 0.5f;
                        slantSet = true;
                        slantFromVariation = true;
                        break;
                    case "slnt":
                        if (!slantSet) italic = MathF.Abs(variation.Value) > 0.01f;
                        slantFromVariation = true;
                        break;
                }
            }
        }

        // DirectWrite fakes bold/italic when the family has no real face. If the user
        // disabled the synthetic style, ask for the closest real face instead. An axis the
        // user set explicitly is not a synthesis request, so it is left untouched.
        if (key == 3 && !weightFromVariation && !slantFromVariation &&
            !config.SyntheticBoldItalic && !HasRealFace(family, weight, italic))
        {
            if (HasRealFace(family, 700, false))
            {
                weight = 700;
                italic = false;
            }
            else if (HasRealFace(family, 400, true))
            {
                weight = 400;
                italic = true;
            }
            else
            {
                weight = 400;
                italic = false;
            }
        }
        else
        {
            if (!weightFromVariation && IsBold(weight) && !config.SyntheticBold &&
                !HasRealFace(family, weight, italic))
            {
                weight = 400;
            }

            if (!slantFromVariation && italic && !config.SyntheticItalic &&
                !HasRealFace(family, weight, italic))
            {
                italic = false;
            }
        }

        return (weight, italic, stretch);
    }

    /// <summary>Bold/italic faces inherit "font-variation" unless they set their own axes.</summary>
    private List<FontVariation>? VariationsFor(int key) =>
        _config?.VariationsFor((key & 1) != 0, (key & 2) != 0);

    private static bool IsBold(ushort weight) => weight >= 600;

    /// <summary>Maps an OpenType "wdth" percentage onto DirectWrite's width classes.</summary>
    private static Windows.UI.Text.FontStretch StretchForWidth(float percent)
    {
        if (percent <= 56f) return Windows.UI.Text.FontStretch.UltraCondensed;
        if (percent <= 68f) return Windows.UI.Text.FontStretch.ExtraCondensed;
        if (percent <= 81f) return Windows.UI.Text.FontStretch.Condensed;
        if (percent <= 93f) return Windows.UI.Text.FontStretch.SemiCondensed;
        if (percent <= 106f) return Windows.UI.Text.FontStretch.Normal;
        if (percent <= 118f) return Windows.UI.Text.FontStretch.SemiExpanded;
        if (percent <= 137f) return Windows.UI.Text.FontStretch.Expanded;
        if (percent <= 175f) return Windows.UI.Text.FontStretch.ExtraExpanded;
        return Windows.UI.Text.FontStretch.UltraExpanded;
    }

    /// <summary>
    /// Whether the system knows a real face for this family, weight and slant. Used only
    /// when synthetic styles are disabled; the answer is cached per face.
    /// </summary>
    private bool HasRealFace(string family, ushort weight, bool italic)
    {
        var key = (family, weight, italic);
        if (_realFaces.TryGetValue(key, out bool cached)) return cached;

        bool found;
        try
        {
            _systemFontSet ??= CanvasFontSet.GetSystemFontSet();
            found = false;
            foreach (CanvasFontFace face in _systemFontSet.Fonts)
            {
                if (face.Weight.Weight != weight) continue;

                bool slanted = face.Style != Windows.UI.Text.FontStyle.Normal;
                if (slanted != italic) continue;
                if (!HasFamilyName(face, family)) continue;

                found = true;
                break;
            }
        }
        catch
        {
            // Without a probe, assume the face exists so DirectWrite keeps its own behavior.
            found = true;
        }

        _realFaces[key] = found;
        return found;
    }

    private static bool HasFamilyName(CanvasFontFace face, string family)
    {
        foreach (KeyValuePair<string, string> pair in face.FamilyNames)
        {
            if (string.Equals(pair.Value, family, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string UseOr(string preferred, string fallback) =>
        string.IsNullOrEmpty(preferred) ? fallback : preferred;

    // ---------------- text layout ----------------
    //
    // Drawing through a cached CanvasTextLayout is what unlocks "font-feature",
    // "font-codepoint-map" and "adjust-icon-height": CanvasTextFormat alone carries no
    // typography and no per-character overrides. The cache is keyed by the exact run text
    // and face, so a row repainted every frame reuses the same layout objects.

    private static readonly CanvasTypographyFeatureName[] StylisticSets =
    {
        CanvasTypographyFeatureName.StylisticSet1, CanvasTypographyFeatureName.StylisticSet2,
        CanvasTypographyFeatureName.StylisticSet3, CanvasTypographyFeatureName.StylisticSet4,
        CanvasTypographyFeatureName.StylisticSet5, CanvasTypographyFeatureName.StylisticSet6,
        CanvasTypographyFeatureName.StylisticSet7, CanvasTypographyFeatureName.StylisticSet8,
        CanvasTypographyFeatureName.StylisticSet9, CanvasTypographyFeatureName.StylisticSet10,
        CanvasTypographyFeatureName.StylisticSet11, CanvasTypographyFeatureName.StylisticSet12,
        CanvasTypographyFeatureName.StylisticSet13, CanvasTypographyFeatureName.StylisticSet14,
        CanvasTypographyFeatureName.StylisticSet15, CanvasTypographyFeatureName.StylisticSet16,
        CanvasTypographyFeatureName.StylisticSet17, CanvasTypographyFeatureName.StylisticSet18,
        CanvasTypographyFeatureName.StylisticSet19, CanvasTypographyFeatureName.StylisticSet20,
    };

    private void InvalidateLayouts()
    {
        foreach (CanvasTextLayout layout in _layouts.Values) layout.Dispose();
        _layouts.Clear();
    }

    /// <summary>
    /// Turns "font-feature" values ("-calt", "ss01", "liga=0") into DirectWrite typography.
    /// Features Win2D does not expose, or values that are not tags, are ignored.
    /// </summary>
    private void BuildTypography()
    {
        _typography = null;
        if (_config is null || _config.FontFeatures.Count == 0) return;

        CanvasTypography? typography = null;
        foreach (string feature in _config.FontFeatures)
        {
            if (!TryParseFontFeature(feature, out CanvasTypographyFeatureName name, out uint parameter))
                continue;

            try
            {
                typography ??= new CanvasTypography();
                typography.AddFeature(name, parameter);
            }
            catch
            {
                // A duplicate feature must not take the renderer down.
            }
        }
        _typography = typography;
    }

    private static bool TryParseFontFeature(string text, out CanvasTypographyFeatureName name, out uint parameter)
    {
        name = CanvasTypographyFeatureName.None;
        parameter = 1;

        string value = text.Trim();
        if (value.Length == 0) return false;

        if (value[0] == '+')
        {
            value = value[1..].TrimStart();
        }
        else if (value[0] == '-')
        {
            value = value[1..].TrimStart();
            parameter = 0;
        }

        int separator = value.IndexOf('=');
        if (separator < 0) separator = value.IndexOf(':');
        if (separator >= 0)
        {
            if (uint.TryParse(value[(separator + 1)..].Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out uint parsed))
            {
                parameter = parsed;
            }
            value = value[..separator].Trim();
        }

        string tag = value.ToLowerInvariant();
        switch (tag)
        {
            case "calt": name = CanvasTypographyFeatureName.ContextualAlternates; break;
            case "liga": name = CanvasTypographyFeatureName.StandardLigatures; break;
            case "clig": name = CanvasTypographyFeatureName.ContextualLigatures; break;
            case "dlig": name = CanvasTypographyFeatureName.DiscretionaryLigatures; break;
            case "hlig": name = CanvasTypographyFeatureName.HistoricalLigatures; break;
            case "rlig": name = CanvasTypographyFeatureName.RequiredLigatures; break;
            case "kern": name = CanvasTypographyFeatureName.Kerning; break;
            case "smcp": name = CanvasTypographyFeatureName.SmallCapitals; break;
            case "c2sc": name = CanvasTypographyFeatureName.SmallCapitalsFromCapitals; break;
            case "pcap": name = CanvasTypographyFeatureName.PetiteCapitals; break;
            case "c2pc": name = CanvasTypographyFeatureName.PetiteCapitalsFromCapitals; break;
            case "case": name = CanvasTypographyFeatureName.CaseSensitiveForms; break;
            case "zero": name = CanvasTypographyFeatureName.SlashedZero; break;
            case "ordn": name = CanvasTypographyFeatureName.Ordinals; break;
            case "frac": name = CanvasTypographyFeatureName.Fractions; break;
            case "afrc": name = CanvasTypographyFeatureName.AlternativeFractions; break;
            case "sups": name = CanvasTypographyFeatureName.Superscript; break;
            case "subs": name = CanvasTypographyFeatureName.Subscript; break;
            case "lnum": name = CanvasTypographyFeatureName.LiningFigures; break;
            case "onum": name = CanvasTypographyFeatureName.OldStyleFigures; break;
            case "pnum": name = CanvasTypographyFeatureName.ProportionalFigures; break;
            case "tnum": name = CanvasTypographyFeatureName.TabularFigures; break;
            case "swsh": name = CanvasTypographyFeatureName.Swash; break;
            case "cswh": name = CanvasTypographyFeatureName.ContextualSwash; break;
            case "salt": name = CanvasTypographyFeatureName.StylisticAlternates; break;
            case "titl": name = CanvasTypographyFeatureName.Titling; break;
            case "unic": name = CanvasTypographyFeatureName.Unicase; break;
            case "hwid": name = CanvasTypographyFeatureName.HalfWidth; break;
            case "fwid": name = CanvasTypographyFeatureName.FullWidth; break;
            case "vert": name = CanvasTypographyFeatureName.VerticalWriting; break;
            case "vrt2": name = CanvasTypographyFeatureName.VerticalAlternatesAndRotation; break;
            default:
                // ss01..ss20; cv01..cv20 has no CanvasTypographyFeatureName equivalent.
                if (tag.Length == 4 && tag[0] == 's' && tag[1] == 's' &&
                    int.TryParse(tag[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int set) &&
                    set is >= 1 and <= 20)
                {
                    name = StylisticSets[set - 1];
                    break;
                }
                return false;
        }
        return true;
    }

    private CanvasTextLayout? GetLayout(string text, bool bold, bool italic)
    {
        if (!UseTextLayout || text.Length == 0) return null;

        var key = new LayoutKey(text, (bold ? 1 : 0) | (italic ? 2 : 0));
        if (_layouts.TryGetValue(key, out CanvasTextLayout? cached)) return cached;

        CanvasTextLayout layout;
        try
        {
            layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), text, FormatFor(bold, italic), 0f, 0f);
        }
        catch
        {
            // No device or an invalid format: the caller falls back to DrawText.
            return null;
        }

        ApplyLayoutOptions(layout, text);

        _layouts[key] = layout;
        return layout;
    }

    /// <summary>Applies the per-character options that a CanvasTextFormat cannot express.</summary>
    private void ApplyLayoutOptions(CanvasTextLayout layout, string text)
    {
        if (_typography is not null)
        {
            try
            {
                layout.SetTypography(0, text.Length, _typography);
            }
            catch
            {
                // Typography is best effort; a rejected feature must not drop the text.
            }
        }

        Config? config = _config;
        if (config is null) return;

        if (!config.IconHeightAdjust.IsZero)
        {
            float iconSize = config.IconHeightAdjust.Apply(_fontSize, _rasterizationScale);
            if (iconSize > 0.1f)
            {
                for (int i = 0; i < text.Length; i += CharLength(text, i))
                {
                    if (!IsPrivateUse(CodepointAt(text, i))) continue;
                    try
                    {
                        layout.SetFontSize(i, CharLength(text, i), iconSize);
                    }
                    catch
                    {
                        // Out-of-range indices only happen for malformed clusters.
                    }
                }
            }
        }

        foreach (FontCodepointMap map in config.FontCodepointMap)
        {
            for (int i = 0; i < text.Length; i += CharLength(text, i))
            {
                int codepoint = CodepointAt(text, i);
                if (codepoint < map.Start || codepoint > map.End) continue;

                try
                {
                    layout.SetFontFamily(i, CharLength(text, i), map.Family);
                }
                catch
                {
                    // A family name DirectWrite cannot resolve keeps the default face.
                }
            }
        }
    }

    private static int CharLength(string text, int index) =>
        char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;

    private static int CodepointAt(string text, int index) =>
        CharLength(text, index) == 2 ? char.ConvertToUtf32(text[index], text[index + 1]) : text[index];

    /// <summary>Nerd Font icons live in the Unicode private use areas.</summary>
    private static bool IsPrivateUse(int codepoint) =>
        codepoint is >= 0xE000 and <= 0xF8FF ||
        codepoint is >= 0xF0000 and <= 0xFFFFD ||
        codepoint is >= 0x100000 and <= 0x10FFFD;

    /// <summary>
    /// Draws a run through the layout cache, falling back to the legacy DrawText call when a
    /// layout could not be created.
    /// </summary>
    private void DrawTextRun(CanvasDrawingSession ds, string text, float x, float y, Color color,
        bool bold, bool italic)
    {
        CanvasTextLayout? layout = GetLayout(text, bold, italic);
        if (layout is null) ds.DrawText(text, x, y, color, FormatFor(bold, italic));
        else ds.DrawTextLayout(layout, x, y, color);
    }

    /// <summary>Char index where the cursor cell starts inside a run, or -1 when there is none.</summary>
    private (int CharOffset, int CellOffset) ShapingBreakAt(Run run, bool haveCursor,
        GhosttyNative.GhosttyRenderStateCursor cursor, int viewportY)
    {
        if (_config is null || !_config.FontShapingBreak) return (-1, 0);
        if (!haveCursor || cursor.ViewportHasValue == 0 || viewportY != cursor.ViewportY)
            return (-1, 0);

        int cell = cursor.ViewportX - run.Col;
        if (cell <= 0 || cell >= run.Cols) return (-1, 0);

        int offset = CharOffsetForCell(run.Text, cell);
        return offset > 0 && offset < run.Text.Length ? (offset, cell) : (-1, 0);
    }

    private static int CharOffsetForCell(string text, int cell)
    {
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        int index = 0;
        while (elements.MoveNext())
        {
            if (index == cell) return elements.ElementIndex;
            index++;
        }
        return -1;
    }

    // ---------------- palette remapping ----------------

    private static Dictionary<int, Rgb>? _lightPalette;

    /// <summary>
    /// The VT reports cell colors resolved from its default (dark) palette. For the light
    /// design theme, map each of those palette colors to the design's light palette so
    /// ANSI output keeps its contrast on a white surface.
    /// </summary>
    private static Dictionary<int, Rgb> LightPalette
    {
        get
        {
            if (_lightPalette is not null) return _lightPalette;

            var baseColors = new Rgb[256];
            GhosttyNative.PaletteDefault(baseColors);

            var source = new Rgb[256];
            Array.Copy(baseColors, source, 256);
            var lightBase = new Rgb[16]
            {
                new(36, 41, 47), new(207, 34, 46), new(17, 99, 41), new(154, 103, 0),
                new(9, 105, 218), new(130, 80, 223), new(27, 124, 131), new(110, 119, 129),
                new(87, 96, 106), new(168, 25, 35), new(26, 127, 55), new(191, 135, 0),
                new(33, 139, 255), new(164, 117, 249), new(49, 146, 170), new(36, 41, 47),
            };
            Array.Copy(lightBase, source, 16);

            var generated = new Rgb[256];
            GhosttyNative.PaletteGenerate(source, default, new Rgb(255, 255, 255), new Rgb(36, 41, 47), false, generated);

            var map = new Dictionary<int, Rgb>(256);
            for (int i = 0; i < 256; i++)
                map[baseColors[i].GetHashCode()] = generated[i];
            _lightPalette = map;
            return map;
        }
    }

    private static Color ThemeColor(Rgb rgb)
    {
        if (Theme.Terminal.Custom || !Theme.Light)
            return Color.FromArgb(255, rgb.R, rgb.G, rgb.B);

        return LightPalette.TryGetValue(rgb.GetHashCode(), out Rgb mapped)
            ? Color.FromArgb(255, mapped.R, mapped.G, mapped.B)
            : Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
    }

    // ---------------- rendering ----------------

    private struct Run
    {
        public int Col;
        public int Cols;
        public string Text;
        public Color Fg;
        public Color Bg;
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public bool Strikethrough;
        public bool Overline;
        public bool Invisible;
        public bool Wide;
    }

    /// <summary>Redraws the whole viewport. <paramref name="cursorOn"/> controls the blink phase.</summary>
    public bool Render(CanvasDrawingSession ds, TerminalSession session, bool cursorOn, bool force,
        float viewportWidth, float viewportHeight)
    {
        IntPtr state = session.RenderState;
        IntPtr terminal = session.Terminal;
        if (state == IntPtr.Zero || terminal == IntPtr.Zero) return false;

        // Drop stale layouts between frames, never while this frame still references them.
        if (_layouts.Count >= LayoutCacheCapacity) InvalidateLayouts();

        // The canvas clears itself before every Draw, so a frame must always be painted.
        bool changed = session.UpdateRenderState();

        // Colors.
        Rgb themeBg = default;
        Rgb themeFg = default;
        Rgb cursorRgb = default;
        bool cursorHasValue = false;
        Color defaultBg;
        Color defaultFg;

        if (GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_COLOR_BACKGROUND, &themeBg) != GhosttyNative.SUCCESS)
            themeBg = new Rgb(7, 9, 14);
        GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_COLOR_FOREGROUND, &themeFg);
        GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_COLOR_CURSOR, &cursorRgb);
        GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_COLOR_CURSOR_HAS_VALUE, &cursorHasValue);

        if (!Theme.Terminal.Custom)
        {
            if (Theme.Light)
            {
                defaultBg = Color.FromArgb(255, 255, 255, 255);
                defaultFg = Color.FromArgb(255, 36, 41, 47);
            }
            else
            {
                themeBg = new Rgb(7, 9, 14);
                defaultBg = Color.FromArgb(255, 7, 9, 14);
                defaultFg = Color.FromArgb(255, themeFg.R, themeFg.G, themeFg.B);
            }
        }
        else
        {
            defaultBg = Color.FromArgb(255, themeBg.R, themeBg.G, themeBg.B);
            defaultFg = Color.FromArgb(255, themeFg.R, themeFg.G, themeFg.B);
        }

        // With "background-opacity" the pane's translucent surface lives in XAML (it also
        // covers the header and the padding), so the canvas starts fully transparent and
        // only adds the image on top.
        float backgroundOpacity = _config is null
            ? 1f
            : Math.Clamp(_config.BackgroundOpacity, 0f, 1f);

        if (backgroundOpacity >= 0.999f) ds.Clear(defaultBg);
        else ds.Clear(Color.FromArgb(0, 0, 0, 0));

        // The IME overlay fills cells to hide the glyphs under the composition; match the
        // pane surface so it does not punch an opaque hole through the translucency.
        Color compositionBg = defaultBg;
        compositionBg.A = (byte)Math.Round(255 * backgroundOpacity);

        // The image sits directly on the surface; cells with the default background are not
        // filled, so the image shows through everywhere a cell is transparent.
        DrawBackgroundImage(ds, viewportWidth, viewportHeight, backgroundOpacity);

        // Cursor.
        var cursor = new GhosttyNative.GhosttyRenderStateCursor
        {
            Size = (nuint)sizeof(GhosttyNative.GhosttyRenderStateCursor),
        };
        bool haveCursor = GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_CURSOR, &cursor) == GhosttyNative.SUCCESS;

        _cursorValid = haveCursor && cursor.ViewportHasValue != 0;
        if (_cursorValid)
        {
            _cursorX = cursor.ViewportX;
            _cursorY = cursor.ViewportY;
        }

        float cellW = _metrics.Width;
        float cellH = _metrics.Height;

        IntPtr rows = IntPtr.Zero;
        IntPtr cells = IntPtr.Zero;
        GhosttyNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out rows);
        GhosttyNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out cells);
        // The iterator is only usable once it is bound to a render state.
        GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_ROW_ITERATOR, &rows);

        var runs = new List<Run>(64);
        string cursorText = string.Empty;
        Color cursorFg = defaultFg;

        try
        {
            while (rows != IntPtr.Zero && GhosttyNative.ghostty_render_state_row_iterator_next(rows))
            {
                int viewportY = 0;
                if (GhosttyNative.ghostty_render_state_row_get(rows, GhosttyNative.ROW_VIEWPORT_Y, &viewportY) != GhosttyNative.SUCCESS)
                    continue;
                if (GhosttyNative.ghostty_render_state_row_get(rows, GhosttyNative.ROW_DATA_CELLS, &cells) != GhosttyNative.SUCCESS)
                    continue;

                var selection = new GhosttyNative.GhosttyRenderStateRowSelection
                {
                    Size = (nuint)sizeof(GhosttyNative.GhosttyRenderStateRowSelection),
                };
                bool hasSelection = GhosttyNative.ghostty_render_state_row_get(rows, GhosttyNative.ROW_DATA_SELECTION, &selection) == GhosttyNative.SUCCESS;

                runs.Clear();
                float rowTop = viewportY * cellH;
                int col = 0;
                bool spacer = false;

                while (GhosttyNative.ghostty_render_state_row_cells_next(cells))
                {
                    var style = new GhosttyNative.GhosttyStyle
                    {
                        Size = (nuint)sizeof(GhosttyNative.GhosttyStyle),
                    };
                    GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_STYLE, &style);

                    Rgb bgRgb = default;
                    Rgb fgRgb = default;
                    bool hasBg = GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_BG_COLOR, &bgRgb) == GhosttyNative.SUCCESS;
                    bool hasFg = GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_FG_COLOR, &fgRgb) == GhosttyNative.SUCCESS;

                    Color bg = defaultBg;
                    if (hasBg)
                        bg = bgRgb == themeBg ? defaultBg : ThemeColor(bgRgb);

                    Color fg = defaultFg;
                    if (hasFg && fgRgb != themeFg)
                        fg = ThemeColor(fgRgb);

                    if (style.Inverse != 0) (bg, fg) = (fg, bg);

                    string text = CellText.Read(cells);

                    // A double-width glyph sits in the first of its two cells and libghostty-vt
                    // leaves the second one empty. That empty cell must not turn into a space
                    // inside the run, or the glyph after it drifts off the grid.
                    bool wide = false;
                    if (spacer)
                    {
                        spacer = false;
                        text = string.Empty;
                    }
                    else if (text.Length > 0 && WideText.IsWide(text))
                    {
                        wide = true;
                        spacer = true;
                    }

                    Color textFg = DefaultFgFor(style, fg, bg, hasFg);

                    // "selection-foreground" replaces the text color inside the selection.
                    if (hasSelection && _config is { HasSelectionForeground: true } config &&
                        col >= selection.StartX && col <= selection.EndX)
                    {
                        Rgb selected = config.SelectionForeground;
                        textFg = Color.FromArgb(255, selected.R, selected.G, selected.B);
                    }

                    var run = new Run
                    {
                        Col = col,
                        Cols = 1,
                        Text = text,
                        Bg = bg,
                        Fg = textFg,
                        Bold = style.Bold != 0,
                        Italic = style.Italic != 0,
                        Underline = style.Underline != 0,
                        Strikethrough = style.Strikethrough != 0,
                        Overline = style.Overline != 0,
                        Invisible = style.Invisible != 0,
                        Wide = wide,
                    };

                    if (haveCursor && cursor.ViewportHasValue != 0 &&
                        viewportY == cursor.ViewportY && col == cursor.ViewportX)
                    {
                        cursorText = text;
                        cursorFg = fg;
                    }

                    bool merged = false;
                    if (runs.Count > 0)
                    {
                        Run prev = runs[^1];
                        if (prev.Fg == run.Fg && prev.Bg == run.Bg && prev.Bold == run.Bold &&
                            prev.Italic == run.Italic && prev.Underline == run.Underline &&
                            prev.Strikethrough == run.Strikethrough && prev.Overline == run.Overline &&
                            prev.Invisible == run.Invisible &&
                            prev.Col + prev.Cols == col)
                        {
                            prev.Text += run.Text;
                            prev.Cols += 1;
                            prev.Wide |= run.Wide;
                            runs[^1] = prev;
                            merged = true;
                        }
                    }
                    if (!merged) runs.Add(run);
                    col++;
                }

                // Backgrounds first so text from neighbouring runs is never clipped.
                // "background-opacity-cells" fades explicit cell backgrounds along with the
                // surface; by default they stay opaque, exactly like Ghostty.
                byte cellAlpha = 255;
                if (_config is { BackgroundOpacityCells: true })
                    cellAlpha = (byte)Math.Round(255 * Math.Clamp(_config.BackgroundOpacity, 0f, 1f));

                foreach (Run run in runs)
                {
                    if (run.Bg != defaultBg)
                    {
                        Color fill = run.Bg;
                        fill.A = cellAlpha;
                        ds.FillRectangle(new Rect(run.Col * cellW, rowTop, run.Cols * cellW, cellH), fill);
                    }
                }

                if (hasSelection)
                {
                    Color color = SelectionFillColor();
                    ds.FillRectangle(new Rect(selection.StartX * cellW, rowTop,
                        (selection.EndX - selection.StartX + 1) * cellW, cellH), color);
                }

                float lineThickness = MathF.Max(1f, MathF.Round(cellH / 16f));
                if (_config is not null)
                    lineThickness = MathF.Max(1f,
                        _config.UnderlineThicknessAdjust.Apply(lineThickness, _rasterizationScale));

                float underlineY = rowTop + cellH - lineThickness - 2;
                float strikethroughY = MathF.Round(rowTop + cellH * 0.5f);
                if (_config is not null)
                {
                    underlineY += _config.UnderlinePositionAdjust.Delta(cellH, _rasterizationScale);
                    strikethroughY += _config.StrikethroughPositionAdjust.Delta(cellH, _rasterizationScale);
                }
                float strikethroughThickness = lineThickness;
                if (_config is not null)
                    strikethroughThickness = MathF.Max(1f,
                        _config.StrikethroughThicknessAdjust.Apply(lineThickness, _rasterizationScale));

                float overlineY = rowTop;
                float overlineThickness = lineThickness;
                if (_config is not null)
                {
                    overlineY += _config.OverlinePositionAdjust.Delta(cellH, _rasterizationScale);
                    overlineThickness = MathF.Max(1f,
                        _config.OverlineThicknessAdjust.Apply(lineThickness, _rasterizationScale));
                }

                foreach (Run run in runs)
                {
                    if (run.Invisible) continue;
                    if (run.Text.Length > 0 && HasInk(run.Text))
                    {
                        float textY = rowTop + _textOffsetY;
                        // A run made only of single-width glyphs advances by exactly one cell
                        // per character in the mono font, so it can be drawn in one call. A run
                        // holding a double-width glyph cannot: the fallback font advances by
                        // its own em, which drifts off the grid, so each grapheme is placed.
                        if (!run.Wide)
                        {
                            // "font-shaping-break = cursor" (the default) draws the cells on
                            // either side of the cursor as two layouts so a ligature cannot
                            // form across it.
                            (int split, int splitCell) = ShapingBreakAt(run, haveCursor, cursor, viewportY);
                            if (split > 0 && split < run.Text.Length)
                            {
                                DrawTextRun(ds, run.Text[..split], run.Col * cellW, textY,
                                    run.Fg, run.Bold, run.Italic);
                                DrawTextRun(ds, run.Text[split..], (run.Col + splitCell) * cellW, textY,
                                    run.Fg, run.Bold, run.Italic);
                            }
                            else
                            {
                                DrawTextRun(ds, run.Text, run.Col * cellW, textY,
                                    run.Fg, run.Bold, run.Italic);
                            }
                        }
                        else
                        {
                            DrawWideRun(ds, run, cellW, textY);
                        }
                    }

                    if (run.Underline)
                    {
                        ds.FillRectangle(new Rect(run.Col * cellW, underlineY,
                            run.Cols * cellW, lineThickness), run.Fg);
                    }
                    if (run.Strikethrough)
                    {
                        ds.FillRectangle(new Rect(run.Col * cellW, strikethroughY,
                            run.Cols * cellW, strikethroughThickness), run.Fg);
                    }
                    if (run.Overline)
                    {
                        ds.FillRectangle(new Rect(run.Col * cellW, overlineY,
                            run.Cols * cellW, overlineThickness), run.Fg);
                    }
                }
            }
        }
        finally
        {
            if (cells != IntPtr.Zero) GhosttyNative.ghostty_render_state_row_cells_free(cells);
            if (rows != IntPtr.Zero) GhosttyNative.ghostty_render_state_row_iterator_free(rows);
        }

        // IME composition: painted over the cells at the cursor so the pinyin being typed is
        // visible while the user picks a candidate, like Windows Terminal does. The buffer is
        // untouched until the composition commits, so this is a pure overlay.
        int compositionEnd = -1;
        if (_cursorValid && _imeComposition.Length > 0)
        {
            compositionEnd = DrawImeComposition(ds, cellW, cellH, _cursorY * cellH + _textOffsetY,
                _cursorX, compositionBg, defaultFg);
        }

        // Cursor. While composing it sits after the composition text, where the IME caret is.
        bool blink = cursorOn || _config?.CursorBlink == false;
        if (haveCursor && cursor.ViewportHasValue != 0 && cursor.Visible != 0 && blink)
        {
            int cursorCol = compositionEnd >= 0 ? compositionEnd : cursor.ViewportX;
            float top = cursor.ViewportY * cellH;
            var rect = new Rect(cursorCol * cellW, top, cellW, cellH);

            Color color = cursorHasValue ? ThemeColor(cursorRgb) : defaultFg;
            float opacity = _config?.CursorOpacity ?? 1.0f;
            color.A = (byte)Math.Clamp(Math.Round(255.0 * opacity), 0, 255);

            string style = CursorStyle(cursor.VisualStyle);
            float height = _config?.CursorHeightAdjust.Apply(cellH, _rasterizationScale) ?? cellH;
            height = Math.Clamp(height, 1f, cellH);

            switch (style)
            {
                case "bar":
                    float barWidth = _config?.CursorThicknessAdjust.Apply(2f, _rasterizationScale) ?? 2f;
                    ds.FillRectangle(new Rect(rect.X, rect.Y, MathF.Max(1f, barWidth), height), color);
                    break;

                case "underline":
                    float thickness = _config?.CursorThicknessAdjust.Apply(2f, _rasterizationScale) ?? 2f;
                    thickness = MathF.Max(1f, thickness);
                    ds.FillRectangle(new Rect(rect.X, rect.Y + cellH - thickness, rect.Width, thickness), color);
                    break;

                case "block_hollow":
                    ds.DrawRectangle(new Rect(rect.X + 0.5, rect.Y + 0.5, rect.Width - 1, rect.Height - 1), color, 1f);
                    break;

                default:
                    ds.FillRectangle(new Rect(rect.X, rect.Y, rect.Width, height), color);
                    if (cursorText.Length > 0)
                    {
                        Color text = CursorTextColor(defaultBg);
                        DrawTextRun(ds, cursorText, (float)rect.X, top + _textOffsetY, text, false, false);
                    }
                    break;
            }
        }

        return true;
    }

    // ---------------- background image ----------------

    private void SetBackgroundImage(string path)
    {
        if (string.Equals(path, _backgroundImagePath, StringComparison.Ordinal)) return;

        _backgroundImagePath = path;
        _backgroundImage?.Dispose();
        _backgroundImage = null;
        if (path.Length == 0) return;

        BackgroundImageLoader.Load(path, ApplyBackgroundImage);
    }

    /// <summary>
    /// Accepts the decoded bitmap, dropping it when the configured path changed while the
    /// load was in flight.
    /// </summary>
    private void ApplyBackgroundImage(string path, CanvasBitmap? bitmap)
    {
        if (!string.Equals(path, _backgroundImagePath, StringComparison.Ordinal))
        {
            bitmap?.Dispose();
            return;
        }

        _backgroundImage?.Dispose();
        _backgroundImage = bitmap;
        if (bitmap is not null) NeedsRedraw?.Invoke();
    }

    /// <summary>
    /// Paints "background-image" with its fit, position, repeat and opacity options. The
    /// drawing session is already clipped to the surface, so "cover" simply draws larger
    /// than the viewport and lets the overflow fall outside.
    /// </summary>
    private void DrawBackgroundImage(CanvasDrawingSession ds, float width, float height,
        float surfaceOpacity)
    {
        CanvasBitmap? image = _backgroundImage;
        Config? config = _config;
        if (image is null || config is null || width <= 0f || height <= 0f) return;

        // Ghostty multiplies the image opacity by background-opacity; the pane surface
        // already contributes that alpha, so the image only adds the remainder.
        float opacity = Math.Clamp(config.BackgroundImageOpacity, 0f, 1f) *
                        Math.Clamp(surfaceOpacity, 0f, 1f);
        var source = new Rect(0, 0, image.Size.Width, image.Size.Height);
        if (opacity <= 0f || source.Width <= 0 || source.Height <= 0) return;

        float imageWidth = (float)source.Width;
        float imageHeight = (float)source.Height;

        if (config.BackgroundImageFit == "stretch")
        {
            ds.DrawImage(image, new Rect(0, 0, width, height), source, opacity,
                CanvasImageInterpolation.Linear);
            return;
        }

        float scale = config.BackgroundImageFit switch
        {
            "cover" => MathF.Max(width / imageWidth, height / imageHeight),
            "none" => 1f,
            _ => MathF.Min(width / imageWidth, height / imageHeight),
        };

        // A zero-sized tile would make the repeat loop spin forever.
        float tileWidth = MathF.Max(1f, imageWidth * scale);
        float tileHeight = MathF.Max(1f, imageHeight * scale);
        (float alignX, float alignY) = ImageAlignment(config.BackgroundImagePosition);
        float originX = (width - tileWidth) * alignX;
        float originY = (height - tileHeight) * alignY;

        if (!config.BackgroundImageRepeat)
        {
            ds.DrawImage(image, new Rect(originX, originY, tileWidth, tileHeight), source,
                opacity, CanvasImageInterpolation.Linear);
            return;
        }

        // Repeat from a grid line so the pattern still lines up with the chosen anchor.
        float startX = originX % tileWidth;
        if (startX > 0f) startX -= tileWidth;
        float startY = originY % tileHeight;
        if (startY > 0f) startY -= tileHeight;

        for (float y = startY; y < height; y += tileHeight)
        {
            for (float x = startX; x < width; x += tileWidth)
            {
                ds.DrawImage(image, new Rect(x, y, tileWidth, tileHeight), source, opacity,
                    CanvasImageInterpolation.Linear);
            }
        }
    }

    /// <summary>Maps one of Ghostty's nine position names to a 0..1 alignment pair.</summary>
    private static (float X, float Y) ImageAlignment(string position) => position switch
    {
        "top-left" => (0f, 0f),
        "top-center" => (0.5f, 0f),
        "top-right" => (1f, 0f),
        "center-left" => (0f, 0.5f),
        "center-right" => (1f, 0.5f),
        "bottom-left" => (0f, 1f),
        "bottom-center" => (0.5f, 1f),
        "bottom-right" => (1f, 1f),
        _ => (0.5f, 0.5f),
    };

    private string CursorStyle(int visualStyle)
    {
        if (_config is not null && _config.CursorStyle.Length > 0) return _config.CursorStyle;
        return visualStyle switch
        {
            GhosttyNative.CURSOR_BAR => "bar",
            GhosttyNative.CURSOR_UNDERLINE => "underline",
            GhosttyNative.CURSOR_BLOCK_HOLLOW => "block_hollow",
            _ => "block",
        };
    }

    private Color CursorTextColor(Color fallback)
    {
        if (_config is { HasCursorText: true })
        {
            Rgb rgb = _config.CursorText;
            return Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
        }
        return fallback;
    }

    /// <summary>Bold color, faint opacity and selection fill all come from the config.</summary>
    private Color DefaultFgFor(GhosttyNative.GhosttyStyle style, Color fg, Color bg, bool explicitColor)
    {
        if (style.Faint != 0)
        {
            float opacity = _config?.FaintOpacity ?? 0.35f;
            return Blend(fg, bg, 1.0f - Math.Clamp(opacity, 0f, 1f));
        }

        if (style.Bold != 0 && !explicitColor && _config is { HasBoldColor: true })
        {
            Rgb rgb = _config.BoldColor;
            return Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
        }

        return fg;
    }

    private Color SelectionFillColor()
    {
        if (_config is { HasSelectionBackground: true })
        {
            Rgb rgb = _config.SelectionBackground;
            return Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
        }

        return Theme.Light
            ? Color.FromArgb(31, 0, 0, 0)
            : Color.FromArgb(64, 255, 255, 255);
    }

    /// <summary>
    /// Paints the in-flight IME composition starting at <paramref name="originCol"/> and
    /// returns the column just past it, where the caret belongs.
    /// </summary>
    private int DrawImeComposition(CanvasDrawingSession ds, float cellW, float cellH,
        float rowTop, int originCol, Color bg, Color fg)
    {
        float underline = MathF.Max(1f, MathF.Round(cellH / 16f));
        int col = originCol;

        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(_imeComposition);
        while (elements.MoveNext())
        {
            string grapheme = elements.GetTextElement();
            if (grapheme.Length == 0) continue;

            int cells = WideText.IsWide(grapheme) ? 2 : 1;
            float x = col * cellW;

            ds.FillRectangle(new Rect(x, rowTop, cells * cellW, cellH), bg);
            DrawTextRun(ds, grapheme, x, rowTop, fg, false, false);
            ds.FillRectangle(new Rect(x, rowTop + cellH - underline - 2, cells * cellW, underline), fg);

            col += cells;
        }

        return col;
    }

    /// <summary>
    /// Draws a run that contains double-width glyphs, placing every grapheme on its own cell
    /// so a fallback font's advance cannot push the rest of the line off the grid.
    /// </summary>
    private void DrawWideRun(CanvasDrawingSession ds, Run run, float cellW, float rowTop)
    {
        int col = run.Col;

        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(run.Text);
        while (elements.MoveNext())
        {
            string grapheme = elements.GetTextElement();
            if (grapheme.Length == 0) continue;

            DrawTextRun(ds, grapheme, col * cellW, rowTop, run.Fg, run.Bold, run.Italic);
            col += WideText.IsWide(grapheme) ? 2 : 1;
        }
    }

    private static bool HasInk(string text)
    {
        foreach (char ch in text)
        {
            if (ch != ' ' && ch != '\u00A0') return true;
        }
        return false;
    }

    private static Color Blend(Color fg, Color bg, float amount)
    {
        static byte Mix(byte a, byte b, float t) => (byte)(a + (b - a) * t);
        return Color.FromArgb(255, Mix(fg.R, bg.R, amount), Mix(fg.G, bg.G, amount), Mix(fg.B, bg.B, amount));
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceEx(string file, uint flags, IntPtr reserved);
}

/// <summary>
/// Decodes "background-image" without blocking the draw callback. It lives outside the
/// renderer because Win2D loads images asynchronously and an await is not allowed inside
/// the renderer's unsafe context.
/// </summary>
internal static class BackgroundImageLoader
{
    public static async void Load(string path, Action<string, CanvasBitmap?> completed)
    {
        CanvasBitmap? bitmap = null;
        try
        {
            bitmap = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), path);
        }
        catch
        {
            // A missing or undecodable file leaves the plain background color in place.
        }

        try
        {
            completed(path, bitmap);
        }
        catch
        {
            bitmap?.Dispose();
        }
    }
}
