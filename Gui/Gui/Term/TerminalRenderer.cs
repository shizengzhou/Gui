using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace Gui.Term;

/// <summary>
/// Win2D renderer for a libghostty-vt render state. No terminal logic lives here.
/// </summary>
public sealed unsafe class TerminalRenderer
{
    private readonly Dictionary<(int Face, string Family), CanvasTextFormat> _formats = new();
    private readonly StringBuilder _text = new(64);

    /// <summary>Cached text layouts, keyed by the run text and the face it is drawn with.</summary>
    private readonly Dictionary<LayoutKey, CachedLayout> _layouts = new();

    /// <summary>
    /// Single-grapheme symbol layouts scaled to fit one cell. Keyed per face/family; the scale
    /// is recomputed on Rebuild so a font-size change cannot leave a stale-sized icon behind.
    /// </summary>
    private readonly Dictionary<(string Text, int Face, string Family), CachedLayout> _symbolLayouts = new();

    /// <summary>
    /// How far a symbol glyph's ink overflows its cell (1 = fits, &lt;1 = shrink). Some Nerd Font
    /// icons draw a full em of ink but advance only one cell, so they spill onto the next letter.
    /// Cached per codepoint/face; cell size changes invalidate it in Rebuild.
    /// </summary>
    private readonly Dictionary<(int Codepoint, int Face), float> _iconScales = new();

    /// <summary>
    /// A cached layout plus the distance from its top to the first baseline. Fallback faces
    /// have their own ascent, so every layout is drawn shifted onto the primary face's
    /// baseline; without that, CJK text sits a few pixels off the Latin text.
    /// </summary>
    private sealed record CachedLayout(CanvasTextLayout Layout, float Baseline);

    /// <summary>The primary face's baseline, in DIPs from the top of its layout.</summary>
    private float _primaryBaseline;

    /// <summary>
    /// Typography for "font-feature"; null when no feature this build understands is set.
    /// </summary>
    private CanvasTypography? _typography;

    /// <summary>An unbounded cache would grow for the lifetime of the window, so it is capped.</summary>
    private const int LayoutCacheCapacity = 1024;

    private readonly Dictionary<(string Family, ushort Weight, bool Italic), bool> _realFaces = new();

    /// <summary>Whether the system knows a family at all, used to drop a style's dead names.</summary>
    private readonly Dictionary<string, bool> _familyExists = new();

    /// <summary>Resolved faces per family/weight/slant, used to test glyph coverage.</summary>
    private readonly Dictionary<(string Family, ushort Weight, bool Italic), CanvasFontFace?> _faces = new();

    /// <summary>Adjusted text colours per foreground/background pair, for "minimum-contrast".</summary>
    private readonly Dictionary<(Rgb Fg, Rgb Bg), Rgb> _contrast = new();

    /// <summary>Per-codepoint coverage answers for the configured families.</summary>
    private readonly Dictionary<(string Family, ushort Weight, bool Italic, int Codepoint), bool> _coverage = new();

    /// <summary>
    /// Size factors for fallback families, from matching their ideograph to two cells. Cached
    /// per family and face because measuring a layout is not free.
    /// </summary>
    private readonly Dictionary<(string Family, ushort Weight, bool Italic), float> _scales = new();

    /// <summary>
    /// Ink height of printable ASCII in the primary face, in DIPs. It stands in for the
    /// ideograph width when the primary font has no ideograph, the way Ghostty estimates it.
    /// </summary>
    private float _asciiHeight;

    /// <summary>
    /// Configured family chains per face (0 regular, 1 bold, 2 italic, 3 bold italic), primary
    /// first. Ghostty lets "font-family" repeat and uses the later entries as fallbacks.
    /// </summary>
    private string[][] _familyChains =
    {
        new[] { "JetBrainsMono Nerd Font" },
        new[] { "JetBrainsMono Nerd Font" },
        new[] { "JetBrainsMono Nerd Font" },
        new[] { "JetBrainsMono Nerd Font" },
    };

    /// <summary>The fallback families for each face, without its primary family.</summary>
    private string[][] _fallbacks = { Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>() };

    /// <summary>
    /// Per-run split into the family each grapheme is drawn with. Null means the run is fully
    /// covered by its primary face, so a single layout (and its ligatures) is used.
    /// </summary>
    private readonly Dictionary<(string Text, int Face), RunSegment[]?> _segments = new();

    /// <summary>"background-image": decoded once and redrawn every frame.</summary>
    private CanvasBitmap? _backgroundImage;
    private string _backgroundImagePath = string.Empty;

    /// <summary>Kitty graphics placements read from the current render state.</summary>
    private readonly List<ImagePlacement> _imagePlacements = new();

    /// <summary>Reused sort scratch for one image layer; never holds data across frames.</summary>
    private readonly List<ImagePlacement> _imageQueue = new();

    /// <summary>Uploaded Kitty images, keyed by image ID and invalidated by generation.</summary>
    private readonly Dictionary<uint, ImageTexture> _imageTextures = new();
    private IntPtr _imageIterator;
    private int _imageFrame;
    private bool _disposed;

    /// <summary>One image rectangle in viewport coordinates, in the library's pixel units.</summary>
    private readonly record struct ImagePlacement(
        uint ImageId,
        ulong Generation,
        int Layer,
        int Z,
        int ViewportCol,
        int ViewportRow,
        uint OffsetX,
        uint OffsetY,
        uint PixelWidth,
        uint PixelHeight,
        uint SourceX,
        uint SourceY,
        uint SourceWidth,
        uint SourceHeight);

    private sealed class ImageTexture
    {
        public required CanvasBitmap Bitmap;
        public ulong Generation;
        public int LastFrame;
    }

    // ---------------- custom shaders ----------------

    /// <summary>Compiled "custom-shader" effects, applied after the terminal scene is drawn.</summary>
    private readonly List<ShaderStage> _shaderEffects = new();
    private readonly List<string> _shaderPaths = new();
    private long _shaderStamp;

    /// <summary>Identifies the newest background build; older results are discarded.</summary>
    private int _shaderBuildVersion;
    private bool _shaderBuildPending;

    /// <summary>Raised on the UI thread when a background build replaced the effect chain.</summary>
    public event Action? ShadersChanged;

    private sealed class ShaderStage
    {
        public required PixelShaderEffect Effect;
        public bool UsesChannel0;
    }

    /// <summary>The offscreen scene that feeds the first shader.</summary>
    private CanvasRenderTarget? _shaderTarget;
    private float _shaderTargetWidth;
    private float _shaderTargetHeight;
    private float _shaderTargetDpi;

    /// <summary>Property names D2D rejected, so they are not retried every frame.</summary>
    private readonly HashSet<string> _shaderUnsupported = new(StringComparer.Ordinal);

    /// <summary>True while at least one custom shader compiled successfully.</summary>
    public bool HasCustomShaders => _shaderEffects.Count > 0;

    private long _shaderStartTicks;
    private float _shaderLastTime;
    private int _shaderFrame;
    private bool _shaderFocused;
    private float _shaderFocusTime;
    private float _shaderCursorChangeTime;

    // Shader uniforms use Ghostty's -X, +Y corner convention. They are kept in DIPs here and
    // converted to physical pixels when uploaded: D2D hands the pixel shader SV_Position in
    // pixels, and Ghostty defines these uniforms in the surface's pixel space, so uploading
    // DIPs rendered the whole surface at 1/scale on a HiDPI display.
    private Vector4 _shaderCursor = new(-1, -1, 0, 0);
    private Vector4 _shaderPreviousCursor = new(-1, -1, 0, 0);
    private Vector4 _shaderCursorColor;
    private Vector4 _shaderPreviousCursorColor;
    private int _shaderCursorStyle;
    private int _shaderPreviousCursorStyle;
    private int _shaderCursorVisible;
    private int _shaderPreviousCursorVisible = -1;
    private Vector3 _shaderBackground;
    private Vector3 _shaderForeground;
    private Vector3 _shaderCursorText;
    private Vector3 _shaderSelectionBackground;
    private Vector3 _shaderSelectionForeground;

    private Vector3[]? _shaderPalette;
    private TerminalTheme? _shaderPaletteTheme;

    /// <summary>Raised when an asynchronously loaded background image becomes available.</summary>
    public event Action? NeedsRedraw;

    /// <summary>One run of graphemes drawn with the same family, positioned on the grid.</summary>
    private readonly record struct RunSegment(int Index, int Length, int Cell, string Family);

    private readonly record struct LayoutKey(string Text, int Face, string Family);

    private CanvasTextFormat _baseFormat = CreateFormat("Consolas", 13f, false, 400);
    private string _fontFamily = "JetBrainsMono Nerd Font";
    private string _resolvedFamily = "JetBrainsMono Nerd Font";
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
    private float _cellAdvanceX = 8f;
    private Dictionary<int, List<SearchSpan>> _searchByRow = new();
    private ushort _cursorX;
    private ushort _cursorY;
    private bool _cursorValid;
    private string _imeComposition = string.Empty;

    /// <summary>The ideograph Ghostty measures a fallback face against (CJK water, U+6C34).</summary>
    private const int IdeographCodepoint = 0x6C34;

    /// <summary>Guards a measurement gone wrong: a fallback may not shrink or grow without bound.</summary>
    private const float MinScale = 0.5f;
    private const float MaxScale = 2.5f;

    /// <summary>
    /// The locale every format carries. It decides which face DirectWrite falls back to for a
    /// glyph the configured families lack: with an English locale a Han character comes back
    /// from a Japanese face, which draws 门 with the stroke crossing the top bar.
    /// </summary>
    private static readonly string _glyphLocale = ResolveGlyphLocale();

    private static string ResolveGlyphLocale()
    {
        string systemDefault = CultureInfo.CurrentUICulture.Name;
        try
        {
            return FontLocale.ForGlyphFallback(
                Windows.System.UserProfile.GlobalizationPreferences.Languages, systemDefault);
        }
        catch
        {
            // Without the language list the process locale is the best answer there is.
            return systemDefault;
        }
    }

    public TerminalRenderer()
    {
        FontCache.RegisterBundledFonts();
        _baseFormat = CreateFormat(_fontFamily, _fontSize, false, 400);
    }

    public CellMetrics CellMetrics => _metrics;

    /// <summary>
    /// The font's exact per-character advance in DIPs. CellMetrics rounds the width up for
    /// column math, but drawing has to use the real advance: runs split at a search
    /// highlight are positioned on the grid, and the rounded width would shift them right
    /// of the continuously drawn text by one pixel per character.
    /// </summary>
    public float CellAdvanceX => _cellAdvanceX;

    /// <summary>
    /// Search highlights for the current viewport. They are applied per cell as a
    /// background and text color override, so the glyphs stay crisp instead of being
    /// covered by a translucent rectangle.
    /// </summary>
    public void SetSearchSpans(IReadOnlyList<SearchSpan>? spans)
    {
        _searchByRow.Clear();
        if (spans is null) return;

        foreach (SearchSpan span in spans)
        {
            if (!_searchByRow.TryGetValue(span.Row, out List<SearchSpan>? row))
            {
                row = new List<SearchSpan>(2);
                _searchByRow[span.Row] = row;
            }
            row.Add(span);
        }
    }

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

    private static CanvasTextFormat CreateFormat(string family, float size, bool italic, ushort weight,
        Windows.UI.Text.FontStretch stretch = Windows.UI.Text.FontStretch.Normal)
    {
        return new CanvasTextFormat
        {
            FontFamily = family,
            FontSize = size,
            // See _glyphLocale: this is the only lever that reaches DirectWrite's fallback.
            LocaleName = _glyphLocale,
            WordWrapping = CanvasWordWrapping.NoWrap,
            FontWeight = new Windows.UI.Text.FontWeight { Weight = weight },
            FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            FontStretch = stretch,
            // DirectDraw text skips color glyphs unless asked: without this an emoji is
            // painted as its monochrome outline, which is not what any other terminal shows.
            Options = CanvasDrawTextOptions.EnableColorFont,
        };
    }

    /// <summary>
    /// Applies the typography and layout options that affect measurement. Colors are read
    /// during <see cref="Render"/>, so they need no separate pass.
    /// </summary>
    public void ApplyConfig(Config config)
    {
        _config = config;

        // A font installed since the last reload has to be seen by the style chains below.
        _familyExists.Clear();
        // The adjusted colours depend on the configured minimum.
        _contrast.Clear();

        // font-style names become a DirectWrite weight/slant per face; "false" disables the
        // style, in which case the regular face is used.
        _faceStyles = new[]
        {
            FontStyleNames.Parse(config.FontStyleName, false, false),
            FontStyleNames.Parse(config.FontStyleBold, true, false),
            FontStyleNames.Parse(config.FontStyleItalic, false, true),
            FontStyleNames.Parse(config.FontStyleBoldItalic, true, true),
        };

        // "font-family" is repeatable in Ghostty: the first entry is the face, the rest are
        // fallbacks for glyphs the earlier entries do not cover. The style-specific lists
        // inherit the regular list when unset.
        string[] regular = Chain(config.FontFamilies);
        // A style-specific family the system cannot resolve draws in the regular chain, the
        // way Ghostty does, instead of letting DirectWrite substitute its own default font.
        string[] bold = StyleChain(config.FontFamiliesBold, regular);
        string[] italic = StyleChain(config.FontFamiliesItalic, regular);
        string[] boldItalic = StyleChain(config.FontFamiliesBoldItalic, bold);
        _familyChains = new[] { regular, bold, italic, boldItalic };

        _fontFamily = regular[0];
        _fontSize = config.FontSizeDip;
        BuildTypography();
        SetBackgroundImage(config.BackgroundImage);
        RebuildShaders(config.CustomShaders);
        Rebuild();
    }

    private static readonly string[] DefaultFamilies = { "JetBrainsMono Nerd Font" };

    private static readonly string[] DefaultFamilyCandidates =
    {
        "JetBrainsMono Nerd Font",
        "Cascadia Mono",
        "Cascadia Code",
        "Consolas",
        "Courier New",
    };

    private static string[] Chain(List<string> families, string[]? inherit = null)
    {
        if (families.Count == 0) return inherit ?? DefaultFamilies;
        var chain = new string[families.Count];
        for (int i = 0; i < families.Count; i++) chain[i] = families[i];
        return chain;
    }

    /// <summary>
    /// The chain a style face draws with. Names the system does not know are dropped, and a
    /// chain with nothing left inherits the one it would have used when the style was unset:
    /// "font-family-bold = FooBar" with no FooBar draws in font-family, not in whatever
    /// DirectWrite falls back to.
    /// </summary>
    private string[] StyleChain(List<string> families, string[] inherit)
    {
        if (families.Count == 0) return inherit;

        var chain = new List<string>(families.Count);
        foreach (string family in families)
        {
            if (FamilyExists(family)) chain.Add(family);
        }

        return chain.Count > 0 ? chain.ToArray() : inherit;
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
        _familyChains[0] = new[] { family };
        Rebuild();
    }

    /// <summary>Font size in DIPs. Recomputes cell metrics.</summary>
    public void SetFontSize(float size)
    {
        if (size < 6f || size > 96f) return;
        if (Math.Abs(size - _fontSize) < 0.01f) return;
        _fontSize = size;
        Rebuild();
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
        Rebuild();
    }

    private void Rebuild()
    {
        // Layouts bake in the face, size and per-range overrides, so any metric change
        // invalidates all of them.
        InvalidateLayouts();
        _segments.Clear();
        _coverage.Clear();
        _faces.Clear();
        _scales.Clear();
        _iconScales.Clear();

        _resolvedFamily = FirstMonospace(_familyChains[0]) ??
                          FirstMonospace(DefaultFamilyCandidates) ?? "Consolas";

        // These are COM wrappers around DirectWrite objects: replacing them without
        // disposing would leave the old ones to the finalizer, which on a small managed
        // heap can take a long time and keep the native memory around.
        foreach (CanvasTextFormat format in _formats.Values) format.Dispose();
        _formats.Clear();
        _baseFormat.Dispose();
        _baseFormat = CreateBaseFormat(_resolvedFamily);
        BuildFallbacks();
        MeasureCell();
    }

    private string? FirstMonospace(string[] families)
    {
        foreach (string family in families)
        {
            if (string.IsNullOrEmpty(family)) continue;
            var probe = CreateFormat(family, _fontSize, false, 400);
            if (!IsMonospace(probe)) continue;
            return family;
        }
        return null;
    }

    /// <summary>Drops the primary family from each chain, leaving the fallback order.</summary>
    private void BuildFallbacks()
    {
        var result = new string[4][];
        for (int face = 0; face < 4; face++)
        {
            string primary = FamilyFor(face);
            var list = new List<string>();
            foreach (string family in _familyChains[face])
            {
                if (string.IsNullOrEmpty(family)) continue;
                if (string.Equals(family, primary, StringComparison.OrdinalIgnoreCase)) continue;
                if (list.Exists(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(family);
            }
            result[face] = list.ToArray();
        }
        _fallbacks = result;
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

        _cellAdvanceX = (float)advance;
        _metrics = new CellMetrics(
            (uint)Math.Max(1, Math.Ceiling(advance)),
            (uint)Math.Max(1, Math.Ceiling(height)));

        // Center the glyph box in the cell, then apply the baseline option (a positive
        // adjustment moves the baseline up, so it subtracts from the top offset).
        float offset = (float)((_metrics.Height - layout.LayoutBounds.Height) / 2.0);
        if (_config is not null) offset -= _config.FontBaselineAdjust.Delta(_metrics.Height, _rasterizationScale);
        _textOffsetY = offset;
        _primaryBaseline = BaselineOf(layout);

        // The primary font's ideograph width, which a fallback is scaled to. A programming
        // font has no ideograph, so Ghostty estimates it from the printable ASCII ink height;
        // measure that once per size change rather than per fallback family.
        using var ascii = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), PrintableAscii, _baseFormat, 0, 0);
        _asciiHeight = (float)ascii.DrawBounds.Height;
        if (_asciiHeight < 0.1f) _asciiHeight = _fontSize * 1.2f;
    }

    /// <summary>The printable ASCII Ghostty measures the fallback scale against.</summary>
    private const string PrintableAscii =
        " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";

    /// <summary>The distance from a layout's top to its first baseline, in DIPs.</summary>
    private static float BaselineOf(CanvasTextLayout layout)
    {
        CanvasLineMetrics[] metrics = layout.LineMetrics;
        return metrics.Length > 0 ? metrics[0].Baseline : 0f;
    }

    /// <summary>The format for a face, with the family it was drawn with.</summary>
    private CanvasTextFormat FormatFor(bool bold, bool italic, string? family = null) =>
        FormatFor((bold ? 1 : 0) | (italic ? 2 : 0), family);

    private CanvasTextFormat FormatFor(int key, string? family = null)
    {
        string resolved = family ?? FamilyFor(key);
        if (key == 0 && string.Equals(resolved, _resolvedFamily, StringComparison.Ordinal))
            return _baseFormat;

        var cacheKey = (key, resolved);
        if (_formats.TryGetValue(cacheKey, out CanvasTextFormat? cached)) return cached;

        (ushort weight, bool slant, Windows.UI.Text.FontStretch stretch) = ResolveFace(key, resolved);
        float size = _fontSize * ScaleFor(resolved, weight, slant);
        var format = CreateFormat(resolved, size, slant, weight, stretch);
        _formats[cacheKey] = format;
        return format;
    }

    /// <summary>
    /// How much a fallback family is scaled so its ideograph fills the two cells a wide glyph
    /// owns. Ghostty normalizes every fallback face by the same metric (its "ic", measured from
    /// 水) against the primary face; a family without an ideograph, such as an emoji or symbol
    /// font, keeps its own size.
    /// </summary>
    private float ScaleFor(string family, ushort weight, bool italic)
    {
        if (string.Equals(family, _resolvedFamily, StringComparison.Ordinal)) return 1f;

        var key = (family, weight, italic);
        if (_scales.TryGetValue(key, out float cached)) return cached;

        float scale = 1f;
        if (HasGlyph(family, weight, italic, IdeographCodepoint) == true)
        {
            try
            {
                using CanvasTextFormat format = CreateFormat(family, _fontSize, italic, weight);
                using var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), "\u6C34", format, 0f, 0f);
                double width = layout.LayoutBounds.Width;
                if (width > 0.1)
                {
                    // The primary face has no ideograph of its own, so the target is Ghostty's
                    // estimate for it: the smaller of the ASCII ink height and two cell widths.
                    float target = MathF.Min(_asciiHeight, 2f * _cellAdvanceX);
                    scale = Math.Clamp((float)(target / width), MinScale, MaxScale);
                }
            }
            catch
            {
                // A face that cannot be measured keeps its own size; the glyph still draws.
                scale = 1f;
            }
        }

        _scales[key] = scale;
        return scale;
    }

    private string FamilyFor(int key) => key == 0 ? _resolvedFamily : _familyChains[key][0];

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
    /// Whether the system knows the family at all, whatever the weight or slant. A style
    /// option that names an unknown family has nothing to draw with, so the regular chain
    /// takes over instead of DirectWrite's own substitution.
    /// </summary>
    private bool FamilyExists(string family)
    {
        string name = family.Trim();
        if (name.Length == 0) return false;
        if (_familyExists.TryGetValue(name, out bool cached)) return cached;

        bool exists;
        try
        {
            CanvasFontSet systemFontSet = FontCache.Get();
            exists = false;
            foreach (CanvasFontFace face in systemFontSet.Fonts)
            {
                if (!HasFamilyName(face, name)) continue;
                exists = true;
                break;
            }
        }
        catch
        {
            // Without a probe, keep the name so DirectWrite resolves it as before.
            exists = true;
        }

        _familyExists[name] = exists;
        return exists;
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
            CanvasFontSet systemFontSet = FontCache.Get();
            found = false;
            foreach (CanvasFontFace face in systemFontSet.Fonts)
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

    // ---------------- font fallback ----------------
    //
    // Ghostty treats every "font-family" entry after the first as a fallback: a glyph the
    // earlier families do not have is drawn with the first one that does. DirectWrite's own
    // system fallback still applies when no configured family covers the glyph.

    /// <summary>The family a grapheme is drawn with on the given face.</summary>
    private string FamilyForGrapheme(string grapheme, int face)
    {
        string primary = FamilyFor(face);
        string[] fallbacks = _fallbacks[face];
        if (fallbacks.Length == 0) return primary;

        // An unknown primary (a family DirectWrite cannot resolve) keeps the single-layout
        // path: DirectWrite's own fallback is better than probing every glyph.
        if (!CoversGrapheme(primary, face, grapheme, assumeWhenUnknown: true))
        {
            // "❤️" asks for the emoji drawing, and the configured families are text faces: a
            // CJK fallback's heart is monochrome, so taking it there would answer the wrong
            // request. The system fallback knows the emoji font, so the grapheme is left to it.
            // (Ghostty picks among its candidates by glyph presentation; Win2D has no way to
            // ask a face whether its glyph for a codepoint is a colour one.)
            if (EmojiPresentation.Asks(grapheme)) return primary;

            foreach (string family in fallbacks)
            {
                if (CoversGrapheme(family, face, grapheme, assumeWhenUnknown: false)) return family;
            }
        }
        return primary;
    }

    /// <summary>Whether the family has a glyph for every meaningful codepoint in the grapheme.</summary>
    private bool CoversGrapheme(string family, int face, string grapheme, bool assumeWhenUnknown)
    {
        (ushort weight, bool italic, _) = ResolveFace(face, family);
        if (FindFace(family, weight, italic) is null) return assumeWhenUnknown;

        for (int i = 0; i < grapheme.Length; i += CharLength(grapheme, i))
        {
            int codepoint = CodepointAt(grapheme, i);
            if (IsIgnorableForCoverage(codepoint)) continue;

            // A probe that threw says nothing about the glyph: fall back to the caller's
            // policy (the primary keeps the glyph, a fallback is skipped) instead of
            // pretending the frame covered it.
            bool? covered = HasGlyph(family, weight, italic, codepoint);
            if (covered is null) return assumeWhenUnknown;
            if (!covered.Value) return false;
        }
        return true;
    }

    /// <summary>Joiners and variation selectors do not need their own glyph in the fallback.</summary>
    private static bool IsIgnorableForCoverage(int codepoint) =>
        codepoint is 0x200C or 0x200D ||
        codepoint is >= 0xFE00 and <= 0xFE0F ||
        codepoint is >= 0xE0100 and <= 0xE01EF;

    private bool? HasGlyph(string family, ushort weight, bool italic, int codepoint)
    {
        var key = (family, weight, italic, codepoint);
        if (_coverage.TryGetValue(key, out bool cached)) return cached;

        if (_coverage.Count >= LayoutCacheCapacity) _coverage.Clear();

        CanvasFontFace? face = FindFace(family, weight, italic);
        if (face is null) return false;

        bool covered;
        try
        {
            covered = face.HasCharacter((uint)codepoint);
        }
        catch
        {
            // DirectWrite could not answer. Remembering that as "has the glyph" would pin
            // the codepoint to this family for the life of the window, so the answer is
            // left uncached and asked again next time.
            return null;
        }

        _coverage[key] = covered;
        return covered;
    }

    /// <summary>The font face DirectWrite would use for a family, weight and slant.</summary>
    private CanvasFontFace? FindFace(string family, ushort weight, bool italic)
    {
        var key = (family, weight, italic);
        if (_faces.TryGetValue(key, out CanvasFontFace? cached)) return cached;

        CanvasFontFace? face = null;
        try
        {
            CanvasFontSet systemFontSet = FontCache.Get();
            face = MatchFace(systemFontSet, family, weight, italic) ??
                   MatchFace(systemFontSet, family, 400, false) ??
                   MatchFace(systemFontSet, family, 700, false);
        }
        catch
        {
            // The system collection could not be reached, so nothing was learned about this
            // family: leave it uncached and ask again rather than pinning the answer.
            return null;
        }

        _faces[key] = face;
        return face;
    }

    private static CanvasFontFace? MatchFace(CanvasFontSet set, string family, ushort weight, bool italic)
    {
        using CanvasFontSet matching = set.GetMatchingFonts(
            family,
            new Windows.UI.Text.FontWeight { Weight = weight },
            Windows.UI.Text.FontStretch.Normal,
            italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal);
        foreach (CanvasFontFace face in matching.Fonts)
        {
            return face;
        }
        return null;
    }

    /// <summary>
    /// Splits a run into segments by the family chosen for each grapheme. Returns null when
    /// the primary face covers everything, which keeps ligatures and shaper behavior intact.
    /// </summary>
    private RunSegment[]? SegmentsFor(string text, int face)
    {
        if (_fallbacks[face].Length == 0) return null;

        var key = (text, face);
        if (_segments.TryGetValue(key, out RunSegment[]? cached)) return cached;

        string primary = FamilyFor(face);
        var segments = new List<RunSegment>(4);
        bool fallback = false;
        int ordinal = 0;

        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string grapheme = elements.GetTextElement();
            if (grapheme.Length == 0) continue;

            string family = FamilyForGrapheme(grapheme, face);
            if (!string.Equals(family, primary, StringComparison.Ordinal)) fallback = true;

            int index = elements.ElementIndex;
            if (segments.Count > 0)
            {
                RunSegment last = segments[^1];
                if (last.Index + last.Length == index &&
                    string.Equals(last.Family, family, StringComparison.Ordinal))
                {
                    segments[^1] = last with { Length = last.Length + grapheme.Length };
                    ordinal++;
                    continue;
                }
            }

            segments.Add(new RunSegment(index, grapheme.Length, ordinal, family));
            ordinal++;
        }

        RunSegment[]? result = fallback ? segments.ToArray() : null;
        if (_segments.Count >= LayoutCacheCapacity) _segments.Clear();
        _segments[key] = result;
        return result;
    }

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
        foreach (CachedLayout cached in _layouts.Values) cached.Layout.Dispose();
        _layouts.Clear();
        foreach (CachedLayout cached in _symbolLayouts.Values) cached.Layout.Dispose();
        _symbolLayouts.Clear();
    }

    /// <summary>
    /// The features every run expects and that a typography object otherwise clears: a terminal
    /// font draws its arrow and comparison ligatures through "calt", and kerning through "kern".
    /// </summary>
    private static readonly string[] DefaultOnFeatures = { "kern", "liga", "clig", "calt", "rlig" };

    /// <summary>
    /// Turns "font-feature" values ("-calt", "ss01", "liga=0") into DirectWrite typography.
    /// Features Win2D does not expose, or values that are not tags, are ignored.
    /// </summary>
    private void BuildTypography()
    {
        _typography?.Dispose();
        _typography = null;
        if (_config is null || _config.FontFeatures.Count == 0) return;

        CanvasTypography? typography = null;
        var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string feature in _config.FontFeatures)
        {
            if (!TryParseFontFeature(feature, out CanvasTypographyFeatureName name, out uint parameter))
                continue;

            named.Add(FeatureTag(feature));
            typography ??= new CanvasTypography();
            try
            {
                typography.AddFeature(name, parameter);
            }
            catch
            {
                // A duplicate feature must not take the renderer down.
            }
        }

        // Nothing the config asked for made it through: attach nothing at all, because an empty
        // typography range would still clear the run's default features for no benefit.
        if (typography is null) return;

        // A range that carries a typography object no longer gets the font's defaults applied,
        // so the ones every run relies on are repeated here unless the config named them.
        foreach (string tag in DefaultOnFeatures)
        {
            if (named.Contains(tag)) continue;
            if (!TryParseFontFeature(tag, out CanvasTypographyFeatureName name, out uint parameter)) continue;
            try
            {
                typography.AddFeature(name, parameter);
            }
            catch
            {
                // Same as above: a feature DirectWrite rejects must not drop the text.
            }
        }

        _typography = typography;
    }

    /// <summary>The OpenType tag of a "font-feature" entry: a sign and an "=value" are stripped.</summary>
    private static string FeatureTag(string text)
    {
        string value = text.Trim();
        if (value.Length == 0) return string.Empty;
        if (value[0] is '+' or '-') value = value[1..].TrimStart();

        int separator = value.IndexOf('=');
        if (separator < 0) separator = value.IndexOf(':');
        if (separator >= 0) value = value[..separator];
        return value.Trim().ToLowerInvariant();
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
                // ss01..ss20 have named members; anything else shaped like an OpenType tag is
                // passed through as one of the enum's undefined values. Win2D stores a member as
                // the tag's bytes in little-endian order (ContextualAlternates is 0x746C6163)
                // and its projection swaps them into the tag DirectWrite compares against, so
                // the four characters are packed the same way here. That is what makes character
                // variants (cv01..cv99) work even though the enum has no member for them.
                if (tag.Length == 4 && tag[0] == 's' && tag[1] == 's' &&
                    int.TryParse(tag[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int set) &&
                    set is >= 1 and <= 20)
                {
                    name = StylisticSets[set - 1];
                    break;
                }

                if (!FontFeatureTag.IsTag(tag)) return false;
                name = (CanvasTypographyFeatureName)unchecked((int)FontFeatureTag.Pack(tag));
                break;
        }
        return true;
    }

    private CachedLayout? GetLayout(string text, bool bold, bool italic, string? family = null)
    {
        if (!UseTextLayout || text.Length == 0) return null;

        int face = (bold ? 1 : 0) | (italic ? 2 : 0);
        var key = new LayoutKey(text, face, family ?? FamilyFor(face));
        if (_layouts.TryGetValue(key, out CachedLayout? cached)) return cached;

        CanvasTextLayout layout;
        try
        {
            layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), text, FormatFor(face, key.Family), 0f, 0f);
            // DrawTextLayout reads the options from the layout, not from the format.
            layout.Options = CanvasDrawTextOptions.EnableColorFont;
        }
        catch
        {
            // No device or an invalid format: the caller falls back to DrawText.
            return null;
        }

        ApplyLayoutOptions(layout, text);

        var entry = new CachedLayout(layout, BaselineOf(layout));
        _layouts[key] = entry;
        return entry;
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
        bool bold, bool italic, string? family = null)
    {
        CachedLayout? cached = GetLayout(text, bold, italic, family);
        if (cached is null)
        {
            ds.DrawText(text, x, y, color, FormatFor(bold, italic, family));
            return;
        }

        // Sit every face on the primary face's baseline, so a fallback font's taller
        // ascent does not lift CJK off the line the Latin text sits on.
        ds.DrawTextLayout(cached.Layout, x, y + _primaryBaseline - cached.Baseline, color);
    }

    /// <summary>
    /// How much to shrink a symbol glyph so its ink fits one cell (1 = no change). Many Nerd
    /// Font icons draw a full em of ink but advance only one cell; drawn in a shared layout the
    /// ink spills onto the following letter. Measured from the font's own glyph metrics in em
    /// units, so it is independent of the current font size or DPI.
    /// </summary>
    private float IconScale(int codepoint, int face, string family)
    {
        var key = (codepoint, face);
        if (_iconScales.TryGetValue(key, out float cached)) return cached;

        float scale = 1f;
        try
        {
            (ushort weight, bool italic, _) = ResolveFace(face, family);
            CanvasFontFace? fontFace = FindFace(family, weight, italic);
            if (fontFace is not null)
            {
                int[] glyphs = fontFace.GetGlyphIndices(new[] { (uint)codepoint });
                if (glyphs.Length > 0 && glyphs[0] >= 0)
                {
                    CanvasGlyphMetrics[] metrics = fontFace.GetGlyphMetrics(glyphs, false);
                    if (metrics.Length > 0)
                    {
                        // DrawBounds.Width is the glyph's ink extent in em units. One cell is
                        // _cellAdvanceX DIPs, which at _fontSize is _cellAdvanceX / _fontSize em.
                        float inkEm = (float)metrics[0].DrawBounds.Width;
                        float cellEm = _cellAdvanceX / _fontSize;
                        if (inkEm > cellEm && cellEm > 0f)
                            scale = MathF.Max(0.15f, cellEm / inkEm);
                    }
                }
            }
        }
        catch
        {
            // A font without glyph metrics keeps its glyph at full size; a spilled icon is
            // cosmetic, so a failed probe must never drop the glyph.
        }

        _iconScales[key] = scale;
        return scale;
    }

    /// <summary>Layout for a single symbol grapheme drawn at a reduced size to fit its cell.</summary>
    private CachedLayout? GetSymbolLayout(string text, int face, string family, float scale)
    {
        var key = (text, face, family);
        if (_symbolLayouts.TryGetValue(key, out CachedLayout? cached)) return cached;
        if (scale >= 0.999f) return null;

        try
        {
            var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), text, FormatFor(face, family), 0f, 0f);
            layout.Options = CanvasDrawTextOptions.EnableColorFont;
            layout.SetFontSize(0, text.Length, _fontSize * scale);
            var entry = new CachedLayout(layout, BaselineOf(layout));
            _symbolLayouts[key] = entry;
            return entry;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Draws a run that contains symbol glyphs one grapheme at a time, each placed on its own
    /// cell boundary. A mixed run drawn as one layout lets a wide icon's ink shift the letters
    /// after it off the grid; placing graphemes by cell keeps the terminal columns aligned.
    /// </summary>
    private void DrawSymbolRun(CanvasDrawingSession ds, Run run, int face, float cellW, float textY)
    {
        var elements = StringInfo.GetTextElementEnumerator(run.Text);
        var graphemes = new List<string>();
        while (elements.MoveNext()) graphemes.Add(elements.GetTextElement());

        for (int cell = 0; cell < graphemes.Count; cell++)
        {
            string grapheme = graphemes[cell];
            if (grapheme.Length == 0) continue;

            int codepoint = CodepointAt(grapheme, 0);
            float x = (run.Col + cell) * cellW;

            if (IsPrivateUse(codepoint))
            {
                string family = FamilyForGrapheme(grapheme, face);
                // A symbol followed by a space has room to overdraw into that cell, the way
                // Windows Terminal does: keep it full size instead of shrinking its ink to
                // fit one cell. Only shrink when the next cell is occupied (or unknown because
                // it belongs to another run), where the ink would cover a real character.
                bool hasRoom = cell + 1 < run.Cols && cell + 1 < graphemes.Count &&
                               string.IsNullOrWhiteSpace(graphemes[cell + 1]);
                float scale = hasRoom ? 1f : IconScale(codepoint, face, family);
                CachedLayout? symbol = scale < 0.999f ? GetSymbolLayout(grapheme, face, family, scale) : null;
                if (symbol is not null)
                {
                    // The reduced font still has its own baseline; sharing it with the line
                    // puts the small glyph at the bottom of the cell. Center its ink on the
                    // line's ink instead, leaving the icon's size untouched.
                    // Full ink-centering overcorrects for a glyph whose DrawBounds covers the
                    // font's line box rather than visible ink, so apply half of it: enough to
                    // lift the icon off the baseline without floating above the text.
                    float inkCenter = (float)((symbol.Layout.DrawBounds.Height - _asciiHeight) * 0.25);
                    ds.DrawTextLayout(symbol.Layout, x,
                        textY + _primaryBaseline - symbol.Baseline + inkCenter, run.Fg);
                }
                else
                    DrawTextRun(ds, grapheme, x, textY, run.Fg, run.Bold, run.Italic, family);
            }
            else
            {
                DrawTextRun(ds, grapheme, x, textY, run.Fg, run.Bold, run.Italic);
            }
        }
    }

    /// <summary>Whether a string contains a private-use (symbol) glyph that needs cell-safe placement.</summary>
    private static bool HasSymbol(string text)
    {
        foreach (char ch in text)
            if (ch >= '\uE000' && ch <= '\uF8FF') return true;
        return false;
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

    /// <summary>A cell whose Powerline rune is drawn as a shape instead of a font glyph.</summary>
    private readonly record struct ShapeCell(int Col, char Rune, Color Fg);

    /// <summary>A cell whose box-drawing character is drawn as strokes instead of a glyph.</summary>
    private readonly record struct BoxCell(int Col, BoxSpec Spec, Color Fg);

    /// <summary>One viewport row's resolved runs, kept for the background and text passes.</summary>
    private sealed class RowRuns
    {
        public int ViewportY;
        public required List<Run> Runs;
        public required List<ShapeCell> Shapes;
        public required List<BoxCell> Boxes;

        /// <summary>
        /// Cells each column of this row spans, from the library's own width (2 for a wide
        /// glyph, 1 for its tail and for narrow text). Null while the row has no wide cell,
        /// which is the case for every row of Latin text.
        /// </summary>
        public byte[]? Widths;
        public bool HasSelection;
        public GhosttyNative.GhosttyRenderStateRowSelection Selection;
    }

    /// <summary>Redraws the whole viewport. <paramref name="cursorOn"/> controls the blink phase.</summary>
    public bool Render(CanvasDrawingSession ds, TerminalSession session, bool cursorOn, bool force,
        float viewportWidth, float viewportHeight, ICanvasResourceCreator resources)
    {
        IntPtr state = session.RenderState;
        if (state == IntPtr.Zero || session.Terminal == IntPtr.Zero) return false;

        // Drop stale layouts between frames, never while this frame still references them.
        if (_layouts.Count >= LayoutCacheCapacity) InvalidateLayouts();

        // The canvas clears itself before every Draw, so a frame must always be painted.
        bool changed = session.UpdateRenderState();

        if (_shaderEffects.Count == 0)
        {
            RenderScene(ds, state, session, cursorOn, viewportWidth, viewportHeight, resources, changed);
            return true;
        }

        EnsureShaderTarget(viewportWidth, viewportHeight);
        if (_shaderTarget is null)
        {
            RenderScene(ds, state, session, cursorOn, viewportWidth, viewportHeight, resources, changed);
            return true;
        }

        using (CanvasDrawingSession inner = _shaderTarget.CreateDrawingSession())
            RenderScene(inner, state, session, cursorOn, viewportWidth, viewportHeight, resources, changed);

        DrawShaderChain(ds, _shaderTarget, viewportWidth, viewportHeight);
        return true;
    }

    /// <summary>Draws the terminal itself, either directly or into the shader scene target.</summary>
    private void RenderScene(CanvasDrawingSession ds, IntPtr state, TerminalSession session,
        bool cursorOn, float viewportWidth, float viewportHeight, ICanvasResourceCreator resources,
        bool changed)
    {
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

        // The design themes set their real surface on the terminal, so the render state carries
        // the same colors programs see: no per-cell fixups are needed here.
        defaultBg = Color.FromArgb(255, themeBg.R, themeBg.G, themeBg.B);
        defaultFg = Color.FromArgb(255, themeFg.R, themeFg.G, themeFg.B);

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

        float cellW = _cellAdvanceX;
        float cellH = _metrics.Height;

        // Kitty graphics below the cell backgrounds sit directly on the window surface.
        // The handle is borrowed from the terminal, so it is re-read for every frame.
        IntPtr graphics = IntPtr.Zero;
        session.TryGetKittyGraphics(out graphics);
        CollectImages(state);
        DrawImageLayer(ds, resources, graphics, GhosttyNative.IMAGE_LAYER_BELOW_BACKGROUND,
            cellW, cellH);

        IntPtr rows = IntPtr.Zero;
        IntPtr cells = IntPtr.Zero;
        GhosttyNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out rows);
        GhosttyNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out cells);
        // The iterator is only usable once it is bound to a render state.
        GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_ROW_ITERATOR, &rows);

        // The viewport width in cells, which sizes the per-row width arrays below.
        ushort viewportCols = 0;
        GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_COLS, &viewportCols);

        var rowRuns = new List<RowRuns>(64);
        string cursorText = string.Empty;
        Color cursorFg = defaultFg;
        // The cursor sits on a wide glyph, which covers two cells: the block has to cover both
        // or a CJK character ends up half inverted.
        bool cursorWide = false;
        // The cursor is on the trailing half of that glyph, so it has to start one cell left.
        bool cursorWideTail = false;

        // Debug aid: a cheap signature of what this frame painted. Two frames with the same
        // signature must look identical, so a screen that alternates points outside the renderer.
        uint signature = 2166136261u;

        // Debug counters: a frame that paints fewer rows than the viewport leaves the rest of
        // the surface blank (the canvas is cleared first), which shows up as flicker.
        int drawnRows = 0;
        int rowBreaks = 0;
        int firstRow = 0;
        int lastRow = -2;
        bool haveFirstRow = false;

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

                if (!haveFirstRow) { firstRow = viewportY; haveFirstRow = true; }
                else if (viewportY != lastRow + 1) rowBreaks++;
                lastRow = viewportY;
                drawnRows++;

                var runs = new List<Run>(16);
                var shapes = new List<ShapeCell>(0);
                var boxes = new List<BoxCell>(0);
                int col = 0;
                bool spacer = false;
                byte[]? widths = null;

                while (GhosttyNative.ghostty_render_state_row_cells_next(cells))
                {
                    var style = new GhosttyNative.GhosttyStyle();
                    GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_STYLE, &style);

                    Rgb bgRgb = default;
                    Rgb fgRgb = default;
                    bool hasBg = GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_BG_COLOR, &bgRgb) == GhosttyNative.SUCCESS;
                    bool hasFg = GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_FG_COLOR, &fgRgb) == GhosttyNative.SUCCESS;

                    Color bg = defaultBg;
                    if (hasBg)
                        bg = bgRgb == themeBg ? defaultBg : Color.FromArgb(255, bgRgb.R, bgRgb.G, bgRgb.B);

                    Color fg = defaultFg;
                    if (hasFg && fgRgb != themeFg)
                        fg = Color.FromArgb(255, fgRgb.R, fgRgb.G, fgRgb.B);

                    if (style.Inverse) (bg, fg) = (fg, bg);

                    string text = CellText.Read(cells);

                    // U+10EEEE is the kitty Unicode placeholder: the cell exists only to
                    // position an image, so the glyph itself is never drawn.
                    if (text.Length > 1 && text[0] == '\uDBFB' && text[1] == '\uDEEE')
                        text = string.Empty;

                    // A double-width glyph sits in the first of its two cells and libghostty-vt
                    // leaves the second one empty. That empty cell must not turn into a space
                    // inside the run, or the glyph after it drifts off the grid. The library's
                    // own width decides: a table kept here would miss emoji it calls wide (a
                    // rocket, a mahjong tile) and the rest of the row would shift a cell left.
                    // ASCII is never double width, so a row of Latin text pays nothing here.
                    int kind = IsAsciiCell(text) ? -1 : CellWide(cells);
                    bool wide = false;
                    int advance = 1;
                    if (spacer)
                    {
                        spacer = false;
                        text = string.Empty;
                    }
                    else if (kind is GhosttyNative.WIDE_WIDE or GhosttyNative.WIDE_SPACER_HEAD ||
                             (kind < 0 && text.Length > 0 && WideText.IsWide(text)))
                    {
                        wide = true;
                        spacer = true;
                        advance = 2;
                    }
                    else if (kind == GhosttyNative.WIDE_SPACER_TAIL)
                    {
                        // The trailing half of a wide glyph; its head carries the text.
                        text = string.Empty;
                    }

                    // The wide run is drawn a grapheme at a time, so it needs the same widths.
                    if (advance > 1 && widths is null)
                        widths = new byte[Math.Max((int)viewportCols, col + 2)];
                    if (widths is not null)
                    {
                        if (col >= widths.Length) Array.Resize(ref widths, Math.Max(col + 1, widths.Length * 2));
                        widths[col] = (byte)advance;
                    }

                    Color textFg = DefaultFgFor(style, fg, bg, hasFg);

                    // "selection-foreground" replaces the text color inside the selection.
                    // The main config wins; otherwise a bundled theme's own selection color.
                    if (hasSelection && col >= selection.StartX && col <= selection.EndX)
                    {
                        if (_config is { HasSelectionForeground: true } config)
                        {
                            Rgb selected = config.SelectionForeground;
                            textFg = Color.FromArgb(255, selected.R, selected.G, selected.B);
                        }
                        else if (Theme.Terminal is { Custom: true, SelectionForegroundSet: true } themeSelection)
                        {
                            Rgb selected = themeSelection.SelectionForeground;
                            textFg = Color.FromArgb(255, selected.R, selected.G, selected.B);
                        }
                    }

                    // Search highlights win over normal and selected cell colors. The selected
                    // match gets its own pair, exactly like Ghostty's find bar.
                    if (_searchByRow.TryGetValue(viewportY, out List<SearchSpan>? searchRow) &&
                        FindSearchSpan(searchRow, col, out bool searchSelected))
                    {
                        Color cellBg = bg;
                        bg = SearchBackgroundColor(searchSelected, cellBg);
                        textFg = SearchForegroundColor(searchSelected, fg, cellBg);
                    }

                    // "minimum-contrast" lifts text that would otherwise sit too close to its
                    // background. Colour emoji keep their own palette, so they are left alone.
                    if (_config is { MinimumContrast: > 1f } && !style.Invisible && !IsEmojiText(text))
                        textFg = ContrastColor(textFg, bg);

                    // Powerline separators and caps are drawn as shapes (see DrawShapeCells):
                    // the font's glyph has a coarse edge at cell height. The space keeps the
                    // cell's width on the grid.
                    if (text.Length == 1 && text[0] is >= '\uE0B0' and <= '\uE0B7' && !style.Invisible)
                    {
                        shapes.Add(new ShapeCell(col, text[0], textFg));
                        text = " ";
                    }

                    // Box drawing is stroked from the cell geometry, the way Ghostty draws
                    // it: the lines land on the cell edges whatever the font's outline does,
                    // and "adjust-box-thickness" gets a thickness to change.
                    if (text.Length == 1 && BoxDrawing.TryGet(text[0], out BoxSpec box) && !style.Invisible)
                    {
                        boxes.Add(new BoxCell(col, box, textFg));
                        text = " ";
                    }

                    var run = new Run
                    {
                        Col = col,
                        Cols = 1,
                        Text = text,
                        Bg = bg,
                        Fg = textFg,
                        Bold = style.Bold,
                        Italic = style.Italic,
                        Underline = style.UnderlineStyle != 0,
                        Strikethrough = style.Strikethrough,
                        Overline = style.Overline,
                        Invisible = style.Invisible,
                        Wide = wide,
                    };

                    if (haveCursor && cursor.ViewportHasValue != 0 && viewportY == cursor.ViewportY)
                    {
                        if (col == cursor.ViewportX)
                        {
                            cursorText = text;
                            cursorFg = fg;
                            cursorWide = wide;
                            cursorWideTail = false;
                        }
                        else if (wide && cursor.WideTail != 0 && col + 1 == cursor.ViewportX)
                        {
                            cursorText = text;
                            cursorFg = fg;
                            cursorWide = true;
                            cursorWideTail = true;
                        }
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

                rowRuns.Add(new RowRuns
                {
                    ViewportY = viewportY,
                    Runs = runs,
                    Shapes = shapes,
                    Boxes = boxes,
                    Widths = widths,
                    HasSelection = hasSelection,
                    Selection = selection,
                });
            }
        }
        finally
        {
            if (cells != IntPtr.Zero) GhosttyNative.ghostty_render_state_row_cells_free(cells);
            if (rows != IntPtr.Zero) GhosttyNative.ghostty_render_state_row_iterator_free(rows);
        }

        Diag.Debug("render.sig", $"drawn={drawnRows} sig={signature:X8} cursor={cursorOn}");

        // How many rows the viewport should hold, from the same math the pane uses. Log only
        // when the frame disagrees: equal frames would flood the log at every keystroke.
        int expectedRows = cellH > 0 ? (int)(viewportHeight / cellH) : 0;
        if (drawnRows != expectedRows || rowBreaks > 0)
        {
            Diag.Debug("render.rows",
                $"drawn={drawnRows} expected={expectedRows} span={firstRow}..{lastRow} breaks={rowBreaks} " +
                $"viewport={viewportWidth:0.#}x{viewportHeight:0.#} cellH={cellH:0.##} changed={changed}");
        }

        // Backgrounds first so text from neighbouring runs is never clipped.
        // "background-opacity-cells" fades explicit cell backgrounds along with the
        // surface; by default they stay opaque, exactly like Ghostty.
        byte cellAlpha = 255;
        if (_config is { BackgroundOpacityCells: true })
            cellAlpha = (byte)Math.Round(255 * Math.Clamp(_config.BackgroundOpacity, 0f, 1f));

        foreach (RowRuns row in rowRuns)
        {
            float rowTop = row.ViewportY * cellH;

            // Background rectangles land on whole device pixels. A boundary at a fractional DIP
            // coordinate makes each neighbour cover half of the shared column, and blending the
            // two halves lands darker than either colour - the same hairline the merge above
            // removes inside one colour, here between two.
            float dpiScale = _rasterizationScale <= 0f ? 1f : _rasterizationScale;
            float Snap(float dip) => MathF.Round(dip * dpiScale) / dpiScale;

            // Runs that share a background fill as one rectangle. A run breaks whenever the
            // foreground, the weight or an underline changes, and painting the pieces of one
            // colour separately leaves a darker hairline wherever the shared edge lands inside
            // a pixel: each rectangle covers about half of that column and SourceOver over the
            // two halves only reaches three quarters of the colour.
            int at = 0;
            while (at < row.Runs.Count)
            {
                Run first = row.Runs[at];
                int end = at + 1;
                while (end < row.Runs.Count && row.Runs[end].Bg == first.Bg) end++;

                if (first.Bg != defaultBg)
                {
                    Run last = row.Runs[end - 1];
                    Color fill = first.Bg;
                    fill.A = cellAlpha;
                    float left = Snap(first.Col * cellW);
                    float right = Snap((last.Col + last.Cols) * cellW);
                    float top = Snap(rowTop);
                    float bottom = Snap(rowTop + cellH);
                    ds.FillRectangle(
                        new Rect(left, top,
                            MathF.Max(1f / dpiScale, right - left),
                            MathF.Max(1f / dpiScale, bottom - top)),
                        fill);
                }

                at = end;
            }

            if (!row.HasSelection) continue;
            Color color = SelectionFillColor();
            ds.FillRectangle(new Rect(row.Selection.StartX * cellW, rowTop,
                (row.Selection.EndX - row.Selection.StartX + 1) * cellW, cellH), color);
        }

        DrawImageLayer(ds, resources, graphics, GhosttyNative.IMAGE_LAYER_BELOW_TEXT, cellW, cellH);

        foreach (RowRuns row in rowRuns)
            DrawRowText(ds, row, cellW, cellH, haveCursor, cursor, ref signature);

        // The drawn Powerline cells go over the text, so a separator's edge covers the
        // neighbouring cell the way the prompt's blocks are built.
        foreach (RowRuns row in rowRuns)
            DrawShapeCells(ds, row, cellW, cellH, _rasterizationScale);

        // Box-drawing strokes sit in the same layer, over the (space) glyph that keeps the
        // cell's width on the grid.
        foreach (RowRuns row in rowRuns)
            DrawBoxCells(ds, row, cellW, cellH);

        DrawImageLayer(ds, resources, graphics, GhosttyNative.IMAGE_LAYER_ABOVE_TEXT, cellW, cellH);
        PruneImages();

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
            float cursorWidth = compositionEnd < 0 && cursorWide ? cellW * 2f : cellW;
            float cursorLeft = (cursorCol - (compositionEnd < 0 && cursorWideTail ? 1 : 0)) * cellW;
            var rect = new Rect(cursorLeft, top, cursorWidth, cellH);

            Color color = cursorHasValue ? Color.FromArgb(255, cursorRgb.R, cursorRgb.G, cursorRgb.B) : defaultFg;
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
                        DrawTextRun(ds, cursorText, (float)rect.X, top + _textOffsetY, text, false, false,
                            FamilyForGrapheme(cursorText, 0));
                    }
                    break;
            }
        }

        if (_shaderEffects.Count > 0)
        {
            CaptureShaderColors(themeBg, themeFg, cursorRgb, cursorHasValue, haveCursor, cursor,
                cursorWide, cursorWideTail, defaultBg, viewportHeight);
        }
    }

    /// <summary>Copies the values the shader uniforms need out of this frame's state.</summary>
    private void CaptureShaderColors(Rgb background, Rgb foreground, Rgb cursorRgb,
        bool cursorHasValue, bool haveCursor, GhosttyNative.GhosttyRenderStateCursor cursor,
        bool cursorWide, bool cursorWideTail, Color defaultBg, float viewportHeight)
    {
        _shaderBackground = Normalize(background);
        _shaderForeground = Normalize(foreground);

        Color cursorColor = cursorHasValue
            ? Color.FromArgb(255, cursorRgb.R, cursorRgb.G, cursorRgb.B)
            : defaultBg;
        _shaderCursorColor = Normalize(cursorColor);
        _shaderCursorText = NormalizeRgb(CursorTextColor(defaultBg));
        _shaderSelectionBackground = NormalizeRgb(SelectionFillColor());

        Rgb selectionForeground = foreground;
        if (_config is { HasSelectionForeground: true } config)
            selectionForeground = config.SelectionForeground;
        else if (Theme.Terminal is { Custom: true, SelectionForegroundSet: true } theme)
            selectionForeground = theme.SelectionForeground;
        _shaderSelectionForeground = Normalize(selectionForeground);

        _shaderCursorVisible = haveCursor && cursor.Visible != 0 ? 1 : 0;
        _shaderCursorStyle = ShaderCursorStyle(cursor.VisualStyle);
        if (!haveCursor || cursor.ViewportHasValue == 0)
        {
            _shaderCursor = Vector4.Zero;
            return;
        }

        // The cursor is drawn on the exact advance, not the rounded cell width used for
        // column math; the two differ by a fraction of a pixel per column, which over a row
        // drifts shader effects a whole cell right of the cursor.
        float cellWidth = _cellAdvanceX;
        float cellHeight = _metrics.Height;
        // A wide glyph owns two cells, and the block cursor covers both. The shader has
        // to see the same rectangle, or effects keyed to the cursor draw a rim through
        // the middle of the character. A cursor on the trailing half starts one left.
        float width = cursorWide ? cellWidth * 2f : cellWidth;
        float left = (cursor.ViewportX - (cursorWideTail ? 1 : 0)) * cellWidth;
        float bottom = viewportHeight - (cursor.ViewportY + 1) * cellHeight;
        _shaderCursor = new Vector4(left, bottom, width, cellHeight);
    }

    private static Vector3 Normalize(Rgb color) =>
        new(color.R / 255f, color.G / 255f, color.B / 255f);

    private static Vector4 Normalize(Color color) =>
        new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

    private static Vector3 NormalizeRgb(Color color) =>
        new(color.R / 255f, color.G / 255f, color.B / 255f);

    private static int ShaderCursorStyle(int visual) => visual switch
    {
        GhosttyNative.CURSOR_BLOCK => 0,
        GhosttyNative.CURSOR_BLOCK_HOLLOW => 1,
        GhosttyNative.CURSOR_BAR => 2,
        GhosttyNative.CURSOR_UNDERLINE => 3,
        _ => 0,
    };

    /// <summary>
    /// Draws one row's glyphs and decorations. Backgrounds and images were already painted,
    /// so this pass only adds text.
    /// </summary>
    /// <summary>
    /// Draws the cells that hold a Powerline rune (U+E0B0..U+E0B7) as shapes. Those blocks and
    /// caps have to sit exactly on the cell edges, which a font's outline rarely does, and Kero
    /// and Ghostty draw them for the same reason: a filled triangle for U+E0B0/U+E0B2 and half
    /// an ellipse for U+E0B4..U+E0B7, stroked instead of filled for the odd rune of each pair.
    /// </summary>
    private static void DrawShapeCells(CanvasDrawingSession ds, RowRuns row, float cellW, float cellH,
        float scale)
    {
        if (row.Shapes.Count == 0) return;

        float s = scale <= 0f ? 1f : scale;
        float Snap(float dip) => MathF.Round(dip * s) / s;
        float stroke = MathF.Max(1f, MathF.Round(s)) / s;

        float rowTop = Snap(row.ViewportY * cellH);
        float rowBottom = Snap((row.ViewportY + 1) * cellH);
        float midY = (rowTop + rowBottom) / 2f;

        foreach (ShapeCell cell in row.Shapes)
        {
            float left = Snap(cell.Col * cellW);
            float right = Snap((cell.Col + 1) * cellW);
            bool filled = cell.Rune is '\uE0B0' or '\uE0B2' or '\uE0B4' or '\uE0B6';

            using var path = new CanvasPathBuilder(ds);
            switch (cell.Rune)
            {
                case '\uE0B0':
                case '\uE0B1':
                    path.BeginFigure(left, rowTop);
                    path.AddLine(right, midY);
                    path.AddLine(left, rowBottom);
                    break;

                case '\uE0B2':
                case '\uE0B3':
                    path.BeginFigure(right, rowTop);
                    path.AddLine(left, midY);
                    path.AddLine(right, rowBottom);
                    break;

                default:
                {
                    // The half ellipse is emitted as a finely divided polyline: D2D flattens a
                    // curve with a tolerance of a quarter DIP, which at 2x leaves only a handful
                    // of facets on a cell-high cap - each facet has one flat coverage value, and
                    // the arc reads as a staircase instead of a curve.
                    float tip = cell.Rune is '\uE0B6' or '\uE0B7' ? left : right;
                    float flat = cell.Rune is '\uE0B6' or '\uE0B7' ? right : left;
                    float ry = (rowBottom - rowTop) / 2f;
                    const int segments = 48;
                    path.BeginFigure(flat, rowTop);
                    for (int i = 1; i <= segments; i++)
                    {
                        float phi = MathF.PI * i / segments;
                        path.AddLine(flat + (tip - flat) * MathF.Sin(phi), midY - ry * MathF.Cos(phi));
                    }
                    break;
                }
            }

            path.EndFigure(filled ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);

            using CanvasGeometry geometry = CanvasGeometry.CreatePath(path);
            if (filled) ds.FillGeometry(geometry, cell.Fg);
            else ds.DrawGeometry(geometry, cell.Fg, stroke);
        }
    }

    /// <summary>
    /// Draws the box-drawing cells as strokes instead of glyphs. Every edge is snapped to the
    /// device pixel grid, a double line is two strokes with a gap of one thickness, and a
    /// junction joins the matching strokes, so tiled boxes have no seams or steps.
    /// </summary>
    private void DrawBoxCells(CanvasDrawingSession ds, RowRuns row, float cellW, float cellH)
    {
        if (row.Boxes.Count == 0) return;

        float s = _rasterizationScale <= 0f ? 1f : _rasterizationScale;
        float pixel = 1f / s;
        float Snap(float dip) => MathF.Round(dip * s) / s;

        float rowTop = Snap(row.ViewportY * cellH);
        float rowBottom = Snap((row.ViewportY + 1) * cellH);

        // The font's own box strokes are about one pixel at a 16 DIP cell; keep that, and let
        // "adjust-box-thickness" move it the way the other adjust-* options do.
        float light = MathF.Max(pixel, Snap(MathF.Max(1f, MathF.Round(cellH * 0.0625f * s) / s)));
        if (_config is not null)
            light = MathF.Max(pixel, Snap(_config.BoxThicknessAdjust.Apply(light, s)));
        float heavy = MathF.Max(light + pixel, Snap(light * 2f));
        float offset = light; // the band centres of a double line sit one thickness apart

        foreach (BoxCell cell in row.Boxes)
        {
            BoxSpec spec = cell.Spec;
            Color color = cell.Fg;

            float left = Snap(cell.Col * cellW);
            float right = Snap((cell.Col + 1) * cellW);
            float cx = Snap((left + right) / 2f);
            float cy = Snap((rowTop + rowBottom) / 2f);

            bool hasLeft = spec.Left != BoxWeight.None;
            bool hasRight = spec.Right != BoxWeight.None;
            bool hasUp = spec.Up != BoxWeight.None;
            bool hasDown = spec.Down != BoxWeight.None;

            if (spec.Diagonal != BoxDiagonal.None)
            {
                if (spec.Diagonal != BoxDiagonal.Backward)
                    ds.DrawLine(new Vector2(left, rowBottom), new Vector2(right, rowTop), color, light);
                if (spec.Diagonal != BoxDiagonal.Forward)
                    ds.DrawLine(new Vector2(left, rowTop), new Vector2(right, rowBottom), color, light);
                continue;
            }

            void Horizontal(float from, float to, float centerY, float thickness)
            {
                float x0 = Snap(MathF.Min(from, to));
                float x1 = Snap(MathF.Max(from, to));
                if (x1 - x0 < pixel || thickness <= 0f) return;
                ds.FillRectangle(new Rect(x0, Snap(centerY - thickness / 2f), x1 - x0, thickness), color);
            }

            void Vertical(float centerX, float from, float to, float thickness)
            {
                float y0 = Snap(MathF.Min(from, to));
                float y1 = Snap(MathF.Max(from, to));
                if (y1 - y0 < pixel || thickness <= 0f) return;
                ds.FillRectangle(new Rect(Snap(centerX - thickness / 2f), y0, thickness, y1 - y0), color);
            }

            void Dashed(float from, float to, float center, float thickness, int count, bool vertical)
            {
                float dash = MathF.Abs(to - from) / (2 * count - 1);
                float step = to >= from ? dash : -dash;
                float start = from;
                for (int i = 0; i < count; i++)
                {
                    if (vertical) Vertical(center, start, start + step, thickness);
                    else Horizontal(start, start + step, center, thickness);
                    start += step * 2;
                }
            }

            float Thickness(BoxWeight weight) => weight switch
            {
                BoxWeight.Heavy => heavy,
                BoxWeight.Light => light,
                _ => 0f,
            };

            if (spec.Rounded)
            {
                // The four ARC characters have one horizontal and one vertical arm. The turn
                // is a quarter circle centred inside it, so the arms stay on the cell's centre
                // lines and only the corner is rounded.
                float sx = hasRight ? 1f : -1f;
                float sy = hasDown ? 1f : -1f;
                float radius = MathF.Min(right - left, rowBottom - rowTop) * 0.45f;

                if (hasRight) Horizontal(cx, right, cy, light);
                else Horizontal(left, cx, cy, light);
                if (hasDown) Vertical(cx, cy, rowBottom, light);
                else Vertical(cx, rowTop, cy, light);

                float arcX = cx + sx * radius;
                float arcY = cy + sy * radius;
                float start = MathF.Atan2(-sy, 0f);
                float sweep = MathF.Atan2(0f, -sx) - start;
                while (sweep > MathF.PI) sweep -= MathF.Tau;
                while (sweep < -MathF.PI) sweep += MathF.Tau;

                const int segments = 12;
                var previous = new Vector2(arcX, cy);
                for (int i = 1; i <= segments; i++)
                {
                    float angle = start + sweep * i / segments;
                    var point = new Vector2(arcX + radius * MathF.Cos(angle),
                        arcY + radius * MathF.Sin(angle));
                    ds.DrawLine(previous, point, color, light);
                    previous = point;
                }
                continue;
            }

            bool horizontalDouble = spec.HorizontalDouble;
            bool verticalDouble = spec.VerticalDouble;
            // A true cross leaves the small hole the double-line glyphs have; every other
            // junction keeps the through axis continuous.
            bool hole = horizontalDouble && verticalDouble && hasLeft && hasRight && hasUp && hasDown;

            if (horizontalDouble)
            {
                for (int band = 0; band < 2; band++)
                {
                    float shift = band == 0 ? -offset : offset;
                    float y = cy + shift;

                    if (hole)
                    {
                        Horizontal(left, cx - offset + light / 2f, y, light);
                        Horizontal(cx + offset - light / 2f, right, y, light);
                        continue;
                    }

                    float from = hasLeft ? left : EndingArm(cx, shift, true, verticalDouble, hasUp, hasDown, light);
                    float to = hasRight ? right : EndingArm(cx, shift, false, verticalDouble, hasUp, hasDown, light);
                    Horizontal(from, to, y, light);
                }
            }
            else if (spec.Dashes > 0)
            {
                Dashed(left, right, cy, Thickness(hasLeft ? spec.Left : spec.Right), spec.Dashes, false);
            }
            else
            {
                if (hasLeft) Horizontal(left, cx, cy, Thickness(spec.Left));
                if (hasRight) Horizontal(cx, right, cy, Thickness(spec.Right));
            }

            if (verticalDouble)
            {
                for (int band = 0; band < 2; band++)
                {
                    float shift = band == 0 ? -offset : offset;
                    float x = cx + shift;

                    if (hole)
                    {
                        Vertical(x, rowTop, cy - offset + light / 2f, light);
                        Vertical(x, cy + offset - light / 2f, rowBottom, light);
                        continue;
                    }

                    float from = hasUp ? rowTop : EndingArm(cy, shift, true, horizontalDouble, hasLeft, hasRight, light);
                    float to = hasDown ? rowBottom : EndingArm(cy, shift, false, horizontalDouble, hasLeft, hasRight, light);
                    Vertical(x, from, to, light);
                }
            }
            else if (spec.Dashes > 0)
            {
                Dashed(rowTop, rowBottom, cx, Thickness(hasUp ? spec.Up : spec.Down), spec.Dashes, true);
            }
            else
            {
                if (hasUp) Vertical(cx, rowTop, cy, Thickness(spec.Up));
                if (hasDown) Vertical(cx, cy, rowBottom, Thickness(spec.Down));
            }
        }
    }

    /// <summary>
    /// Where a double line's stroke ends when it has an arm on one side only. The stroke joins
    /// the matching stroke of the perpendicular pair: the near one at a T junction, and the one
    /// at the same offset around a corner, which is how the double-line glyphs are drawn.
    /// </summary>
    private static float EndingArm(float center, float shift, bool armPointsPositive,
        bool perpendicularDouble, bool perpendicularUp, bool perpendicularDown, float thickness)
    {
        if (!perpendicularDouble) return center;

        // At a T the stroke joins the near stroke of the perpendicular pair; around a corner
        // it joins the one at its own offset. Either way it has to reach the far edge of that
        // stroke, or the joint shows a notch half a thickness wide.
        float join = perpendicularUp && perpendicularDown
            ? center + (armPointsPositive ? MathF.Abs(shift) : -MathF.Abs(shift))
            : center + shift;

        return join + (armPointsPositive ? -thickness / 2f : thickness / 2f);
    }

    /// <summary>
    /// The text colour a cell should use so that it meets "minimum-contrast" against that
    /// cell's own background. The same few colour pairs repeat across a screen, so the answer
    /// is cached per pair.
    /// </summary>
    private Color ContrastColor(Color foreground, Color background)
    {
        float minimum = _config?.MinimumContrast ?? 1f;
        if (minimum <= 1f) return foreground;

        var key = (new Rgb(foreground.R, foreground.G, foreground.B),
            new Rgb(background.R, background.G, background.B));
        if (_contrast.TryGetValue(key, out Rgb cached))
            return Color.FromArgb(foreground.A, cached.R, cached.G, cached.B);

        if (_contrast.Count >= LayoutCacheCapacity) _contrast.Clear();

        Rgb raised = Contrast.Raise(key.Item1, key.Item2, minimum);
        _contrast[key] = raised;
        return Color.FromArgb(foreground.A, raised.R, raised.G, raised.B);
    }

    /// <summary>
    /// Whether the cell holds emoji. They draw with their own colours, so Ghostty leaves them
    /// out of "minimum-contrast"; the ranges below cover the emoji blocks DirectWrite can pick
    /// for a cell.
    /// </summary>
    private static bool IsEmojiText(string text)
    {
        if (text.Length == 0 || !Rune.TryGetRuneAt(text, 0, out Rune rune)) return false;

        int codepoint = rune.Value;
        return codepoint is (>= 0x1F000 and <= 0x1FAFF)
            or (>= 0x2600 and <= 0x27BF)
            or (>= 0x2B00 and <= 0x2BFF);
    }

    private void DrawRowText(CanvasDrawingSession ds, RowRuns row, float cellW, float cellH,
        bool haveCursor, in GhosttyNative.GhosttyRenderStateCursor cursor, ref uint signature)
    {
        float rowTop = row.ViewportY * cellH;
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

        foreach (Run run in row.Runs)
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
                    signature = (signature ^ (uint)(row.ViewportY * 131 + run.Col)) * 16777619u;
                    signature = (signature ^ (uint)(run.Fg.R << 16 | run.Fg.G << 8 | run.Fg.B)) * 16777619u;
                    signature = (signature ^ (uint)run.Text.Length) * 16777619u;

                    int face = (run.Bold ? 1 : 0) | (run.Italic ? 2 : 0);

                    // A symbol glyph may draw more ink than the one cell it advances. Draw the
                    // run a grapheme at a time so each glyph starts on its own cell and scale
                    // oversized icons down to fit; a plain run keeps its single shaped layout.
                    if (HasSymbol(run.Text))
                    {
                        DrawSymbolRun(ds, run, face, cellW, textY);
                    }
                    else
                    {
                        // "font-shaping-break = cursor" (the default) draws the cells on either
                        // side of the cursor as two layouts so a ligature cannot form across it.
                        (int split, int splitCell) = ShapingBreakAt(run, haveCursor, cursor, row.ViewportY);

                        RunSegment[]? segments = SegmentsFor(run.Text, face);
                        if (segments is not null)
                        {
                        // Some graphemes need a fallback family, so each segment is
                        // placed on the grid instead of relying on the font advance.
                        // A segment is already a shaping break of its own; the one the
                        // cursor falls inside is split again.
                        DrawSegmentRun(ds, run, segments, split, splitCell, cellW, textY);
                    }
                    else
                    {
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
                    }
                }
                else
                {
                    DrawWideRun(ds, run, row.Widths, cellW, textY);
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

    // ---------------- custom shaders ----------------

    /// <summary>
    /// Recompiles "custom-shader" only when the path list or a file timestamp changed.
    /// Compilation runs on a worker thread and the current effect chain keeps drawing until
    /// the replacement is ready, so a reload never stalls the UI thread. Shader failures are
    /// logged and skipped; the terminal keeps rendering without them.
    /// </summary>
    private void RebuildShaders(List<string> paths)
    {
        long stamp = 17;
        foreach (string path in paths)
        {
            long part = 0;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) part = info.LastWriteTimeUtc.Ticks ^ info.Length;
            }
            catch
            {
                part = 0;
            }
            stamp = unchecked(stamp * 31 + part);
        }

        bool same = paths.Count == _shaderPaths.Count;
        for (int i = 0; same && i < paths.Count; i++)
            same = string.Equals(paths[i], _shaderPaths[i], StringComparison.OrdinalIgnoreCase);
        if (same && stamp == _shaderStamp &&
            (_shaderBuildPending || _shaderEffects.Count == paths.Count))
        {
            return;
        }

        _shaderStamp = stamp;
        _shaderBuildPending = true;
        int version = ++_shaderBuildVersion;
        _shaderPaths.Clear();
        _shaderPaths.AddRange(paths);

        // The bytecode compiles on a worker thread; the Win2D effects are created on the
        // dispatcher thread that owns them once the whole chain is ready.
        DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
        string[] snapshot = paths.ToArray();
        _ = CustomShader.CompileAllAsync(snapshot).ContinueWith(
            build => FinishShaderBuild(build, snapshot, version, queue),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void FinishShaderBuild(Task<CustomShader.Compiled?[]> build, string[] paths,
        int version, DispatcherQueue? queue)
    {
        CustomShader.Compiled?[] compiled;
        try
        {
            compiled = build.Result;
        }
        catch (Exception error)
        {
            Diag.LogException("shader", error);
            compiled = new CustomShader.Compiled?[paths.Length];
        }

        void Apply() => Diag.Guard("shader.apply", () => ApplyShaderBuild(paths, compiled, version));
        if (queue is null)
        {
            Apply();
            return;
        }

        if (!queue.TryEnqueue(DispatcherQueuePriority.Low, Apply))
            Diag.Warn("shader", "dispatcher unavailable; shader build dropped");
    }

    /// <summary>
    /// Swaps in a finished background build on the UI thread. A build that was superseded by
    /// a newer reload or by <see cref="DisposeShaders"/> is dropped unused.
    /// </summary>
    private void ApplyShaderBuild(string[] paths, CustomShader.Compiled?[] compiled, int version)
    {
        if (version != _shaderBuildVersion) return;

        _shaderBuildPending = false;

        // Build the replacement first so the visible chain is swapped in one step and a
        // failure part-way cannot leave the pane without the shaders it already had.
        var stages = new List<ShaderStage>(paths.Length);
        for (int i = 0; i < paths.Length; i++)
        {
            CustomShader.Compiled? shader = compiled[i];
            Diag.Log("shader", shader is null
                ? $"compile failed: {paths[i]}"
                : $"compiled {paths[i]} ({shader.Bytecode.Length} bytes)");
            if (shader is null) continue;

            try
            {
                var effect = new PixelShaderEffect(shader.Bytecode)
                {
                    // Shadertoy shaders may sample iChannel0 away from the current pixel.
                    Source1Mapping = SamplerCoordinateMapping.Unknown,
                };
                stages.Add(new ShaderStage
                {
                    Effect = effect,
                    UsesChannel0 = shader.UsesChannel0,
                });
            }
            catch (Exception error)
            {
                Diag.LogException("shader", error);
            }
        }

        ReleaseShaderEffects();
        _shaderEffects.AddRange(stages);

        // Nothing left to composite through: release the offscreen surfaces as well.
        if (_shaderEffects.Count == 0)
        {
            _shaderTarget?.Dispose();
            _shaderTarget = null;
            DisposePingPong();
        }

        Diag.Log("shader", $"{_shaderEffects.Count} effect(s) from {paths.Length} path(s)");
        _shaderStartTicks = Environment.TickCount64;
        _shaderLastTime = 0f;
        _shaderFrame = 0;
        _shaderPreviousCursorVisible = -1;
        ShadersChanged?.Invoke();
    }

    /// <summary>Releases the compiled effects but keeps the offscreen targets and palette.</summary>
    private void ReleaseShaderEffects()
    {
        foreach (ShaderStage stage in _shaderEffects) stage.Effect.Dispose();
        _shaderEffects.Clear();
        _shaderUnsupported.Clear();
    }

    /// <summary>Tells the shaders when this pane gained focus (for iFocus/iTimeFocus).</summary>
    public void SetFocused(bool focused)
    {
        if (_shaderFocused == focused) return;
        _shaderFocused = focused;
        if (focused) _shaderFocusTime = ShaderTime();
    }

    /// <summary>
    /// Creates the offscreen scene texture from Layout, outside the CanvasControl draw pass,
    /// so the draw callback only records into an already-sized target.
    /// </summary>
    public void PrepareShaderTarget(float width, float height)
    {
        if (HasCustomShaders) EnsureShaderTarget(width, height);
    }

    private void EnsureShaderTarget(float width, float height)
    {
        if (width <= 0f || height <= 0f) return;

        float dpi = 96f * (_rasterizationScale <= 0f ? 1f : _rasterizationScale);
        if (_shaderTarget is not null &&
            MathF.Abs(width - _shaderTargetWidth) < 0.5f &&
            MathF.Abs(height - _shaderTargetHeight) < 0.5f &&
            MathF.Abs(dpi - _shaderTargetDpi) < 0.01f)
        {
            return;
        }

        _shaderTarget?.Dispose();
        _shaderTarget = null;
        DisposePingPong();
        try
        {
            _shaderTarget = new CanvasRenderTarget(
                CanvasDevice.GetSharedDevice(), width, height, dpi);
            _shaderTargetWidth = width;
            _shaderTargetHeight = height;
            _shaderTargetDpi = dpi;
        }
        catch (Exception error)
        {
            Diag.LogException("shader.target", error);
        }
    }

    // ---------------- custom shaders: ping-pong targets ----------------

    /// <summary>
    /// Two offscreen targets sized like the scene target. Shader stages write one while
    /// sampling the other, so no stage ever reads the surface it draws into.
    /// </summary>
    private readonly List<CanvasRenderTarget> _shaderPingPong = new();

    private void DisposePingPong()
    {
        foreach (CanvasRenderTarget target in _shaderPingPong) target.Dispose();
        _shaderPingPong.Clear();
    }

    /// <summary>
    /// Returns a target distinct from <paramref name="current"/> to write this stage's
    /// output into, creating the pair lazily and rebuilding it after a size change.
    /// </summary>
    private CanvasRenderTarget? NextPingPongTarget(ICanvasImage current)
    {
        if (_shaderTarget is null) return null;
        try
        {
            if (_shaderPingPong.Count < 2 ||
                MathF.Abs(_shaderTargetWidth - (float)_shaderPingPong[0].Size.Width) > 0.5f ||
                MathF.Abs(_shaderTargetHeight - (float)_shaderPingPong[0].Size.Height) > 0.5f ||
                MathF.Abs(_shaderTargetDpi - (float)_shaderPingPong[0].Dpi) > 0.01f)
            {
                DisposePingPong();
                for (int i = 0; i < 2; i++)
                {
                    _shaderPingPong.Add(new CanvasRenderTarget(
                        CanvasDevice.GetSharedDevice(), _shaderTargetWidth, _shaderTargetHeight,
                        _shaderTargetDpi));
                }
            }
        }
        catch (Exception error)
        {
            Diag.LogException("shader.pingpong", error);
            DisposePingPong();
            return null;
        }

        // Whichever target the current source is not.
        int index = 0;
        if (_shaderPingPong.Count >= 2 && ReferenceEquals(current, _shaderPingPong[0])) index = 1;
        return _shaderPingPong[index];
    }

    /// <summary>
    /// Runs the terminal scene through each custom shader in order. Every shader sees the
    /// previous shader's output as iChannel0, exactly like Ghostty's shader chain.
    /// </summary>
    private void DrawShaderChain(CanvasDrawingSession ds, ICanvasImage scene, float width, float height)
    {
        if (_shaderEffects.Count == 0) return;

        float now = ShaderTime();
        float delta = MathF.Max(0f, now - _shaderLastTime);
        _shaderLastTime = now;
        _shaderFrame++;

        bool cursorChanged = _shaderCursor != _shaderPreviousCursor ||
            _shaderCursorStyle != _shaderPreviousCursorStyle ||
            _shaderCursorVisible != _shaderPreviousCursorVisible ||
            _shaderCursorColor != _shaderPreviousCursorColor;
        if (cursorChanged) _shaderCursorChangeTime = now;

        // Pixel space, like Ghostty's own uniforms: uv = fragCoord / iResolution has to map
        // the surface exactly once, and fragCoord arrives in physical pixels.
        float scale = _rasterizationScale <= 0f ? 1f : _rasterizationScale;
        var resolution = new Vector3(width * scale, height * scale, 1f);
        Vector3[]? palette = ShaderPalette();

        ICanvasImage source = scene;
        foreach (ShaderStage stage in _shaderEffects)
        {
            // A shader that never samples iChannel0 declares no input; assigning Source1
            // would be rejected by D2D ("shader inputs: 0").
            stage.Effect.Source1 = stage.UsesChannel0 ? source : null;
            SetShaderProperties(stage.Effect, resolution, now, delta, palette);

            // Win2D rejects a PixelShaderEffect as another effect's Source1 when the
            // command batch flushes (E_NOTFOUND), so every stage lands in its own
            // offscreen target and the next stage samples that target instead.
            CanvasRenderTarget? next = NextPingPongTarget(source);
            if (next is null) continue; // fall back to effect chaining when target creation failed
            using (CanvasDrawingSession stageDs = next.CreateDrawingSession())
            {
                var bounds = new Rect(0, 0, _shaderTargetWidth, _shaderTargetHeight);
                stageDs.DrawImage(stage.Effect, bounds, bounds);
            }
            source = next;
        }

        // An explicit destination gives generator shaders (no iChannel0) a defined extent.
        var finalBounds = new Rect(0, 0, width, height);
        ds.DrawImage(source, finalBounds, finalBounds);

        // iPreviousCursor is the last *different* cursor, not the previous frame's: the
        // shader animation redraws every 16 ms, so overwriting it each frame would leave a
        // trail shader with a zero-length segment and nothing to draw.
        if (cursorChanged)
        {
            _shaderPreviousCursor = _shaderCursor;
            _shaderPreviousCursorStyle = _shaderCursorStyle;
            _shaderPreviousCursorVisible = _shaderCursorVisible;
            _shaderPreviousCursorColor = _shaderCursorColor;
        }
    }

    private float ShaderTime()
    {
        if (_shaderStartTicks == 0) _shaderStartTicks = Environment.TickCount64;
        return (Environment.TickCount64 - _shaderStartTicks) / 1000f;
    }

    /// <summary>Uploads Ghostty's Shadertoy-style uniform block for this frame.</summary>
    private void SetShaderProperties(PixelShaderEffect effect, Vector3 resolution,
        float time, float delta, Vector3[]? palette)
    {
        IDictionary<string, object> properties = effect.Properties;
        Set(properties, "iResolution", resolution);
        Set(properties, "iTime", time);
        Set(properties, "iTimeDelta", delta);
        Set(properties, "iFrameRate", 0f);
        Set(properties, "iFrame", _shaderFrame);
        Set(properties, "iChannelTime", new float[4]);
        Set(properties, "iChannelResolution",
            new[] { resolution, Vector3.Zero, Vector3.Zero, Vector3.Zero });
        Set(properties, "iMouse", Vector4.Zero);
        Set(properties, "iDate", Vector4.Zero);
        Set(properties, "iSampleRate", 0f);
        // The cursor and its cell are tracked in DIPs; the shader draws in pixels.
        float scale = _rasterizationScale <= 0f ? 1f : _rasterizationScale;
        Set(properties, "iCurrentCursor", Scaled(_shaderCursor, scale));
        Set(properties, "iPreviousCursor", Scaled(_shaderPreviousCursor, scale));
        Set(properties, "iCurrentCursorColor", _shaderCursorColor);
        Set(properties, "iPreviousCursorColor", _shaderPreviousCursorColor);
        Set(properties, "iCurrentCursorStyle", _shaderCursorStyle);
        Set(properties, "iPreviousCursorStyle", _shaderPreviousCursorStyle);
        Set(properties, "iCursorVisible", _shaderCursorVisible);
        Set(properties, "iTimeCursorChange", _shaderCursorChangeTime);
        Set(properties, "iTimeFocus", _shaderFocusTime);
        Set(properties, "iFocus", _shaderFocused ? 1 : 0);
        if (palette is not null) Set(properties, "iPalette", palette);
        Set(properties, "iBackgroundColor", _shaderBackground);
        Set(properties, "iForegroundColor", _shaderForeground);
        Set(properties, "iCursorColor",
            new Vector3(_shaderCursorColor.X, _shaderCursorColor.Y, _shaderCursorColor.Z));
        Set(properties, "iCursorText", _shaderCursorText);
        Set(properties, "iSelectionForegroundColor", _shaderSelectionForeground);
        Set(properties, "iSelectionBackgroundColor", _shaderSelectionBackground);
    }

    /// <summary>DIPs to physical pixels, for every uniform Ghostty defines in pixel space.</summary>
    private static Vector4 Scaled(Vector4 value, float scale) =>
        new(value.X * scale, value.Y * scale, value.Z * scale, value.W * scale);

    private void Set(IDictionary<string, object> properties, string name, object value)
    {
        if (!properties.ContainsKey(name) || _shaderUnsupported.Contains(name)) return;
        try
        {
            properties[name] = value;
        }
        catch (Exception error)
        {
            _shaderUnsupported.Add(name);
            Diag.Warn("shader.property", $"{name}: {error.Message}");
        }
    }

    /// <summary>Builds the 256-entry palette uniform once per theme.</summary>
    private Vector3[]? ShaderPalette()
    {
        TerminalTheme theme = Theme.Terminal;
        if (ReferenceEquals(theme, _shaderPaletteTheme) && _shaderPalette is not null)
            return _shaderPalette;

        // A design theme keeps its palette in the native generator, not in Theme.Terminal.
        Rgb[] source = theme.Design
            ? TerminalTheme.DesignPalette(Theme.PerceivedLuminance(theme.Background) > 0.5)
            : theme.Palette;

        var palette = new Vector3[256];
        for (int i = 0; i < palette.Length; i++) palette[i] = Normalize(source[i]);
        _shaderPalette = palette;
        _shaderPaletteTheme = theme;
        return palette;
    }

    /// <summary>Releases every compiled shader and the offscreen scene texture.</summary>
    public void DisposeShaders()
    {
        // Any build still in flight belongs to the chain being released.
        _shaderBuildVersion++;
        _shaderBuildPending = false;
        ReleaseShaderEffects();
        _shaderPaths.Clear();
        _shaderTarget?.Dispose();
        _shaderTarget = null;
        DisposePingPong();
        _shaderPalette = null;
        _shaderPaletteTheme = null;
    }

    // ---------------- kitty graphics ----------------

    /// <summary>
    /// Reads the image placements of the current render state. The list is rebuilt on every
    /// frame: scrolling changes it, and reading it is cheap compared with uploading pixels.
    /// </summary>
    private void CollectImages(IntPtr state)
    {
        _imagePlacements.Clear();
        if (_imageIterator == IntPtr.Zero &&
            GhosttyNative.ghostty_render_state_image_iterator_new(IntPtr.Zero, out _imageIterator)
                != GhosttyNative.SUCCESS)
        {
            return;
        }

        IntPtr iterator = _imageIterator;
        if (GhosttyNative.ghostty_render_state_get(
                state, GhosttyNative.RS_IMAGE_ITERATOR, &iterator) != GhosttyNative.SUCCESS)
        {
            return;
        }
        _imageIterator = iterator;

        while (GhosttyNative.ghostty_render_state_image_iterator_next(iterator))
        {            int protocol = 0;
            if (GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_PROTOCOL, &protocol) != GhosttyNative.SUCCESS ||
                protocol != GhosttyNative.IMAGE_PROTOCOL_KITTY)
            {
                continue;
            }

            uint imageId = 0, offsetX = 0, offsetY = 0, width = 0, height = 0;
            uint sourceX = 0, sourceY = 0, sourceWidth = 0, sourceHeight = 0;
            ulong generation = 0;
            int layer = 0, z = 0, col = 0, row = 0;

            if (GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_ID, &imageId) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_GENERATION, &generation) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_LAYER, &layer) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_Z, &z) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_VIEWPORT_COL, &col) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_VIEWPORT_ROW, &row) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_CELL_OFFSET_X, &offsetX) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_CELL_OFFSET_Y, &offsetY) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_PIXEL_WIDTH, &width) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_PIXEL_HEIGHT, &height) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_SOURCE_X, &sourceX) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_SOURCE_Y, &sourceY) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_SOURCE_WIDTH, &sourceWidth) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_render_state_image_get(
                    iterator, GhosttyNative.RENDER_IMAGE_SOURCE_HEIGHT, &sourceHeight) != GhosttyNative.SUCCESS)
            {
                continue;
            }

            _imagePlacements.Add(new ImagePlacement(imageId, generation, layer, z, col, row,
                offsetX, offsetY, width, height, sourceX, sourceY, sourceWidth, sourceHeight));
        }
    }

    /// <summary>
    /// Paints the placements of one layer in z order. Values arrive in physical pixels, while
    /// the drawing session works in DIPs, so positions and sizes are divided by the scale.
    /// </summary>
    private void DrawImageLayer(CanvasDrawingSession ds, ICanvasResourceCreator resources,
        IntPtr graphics, int layer, float cellW, float cellH)
    {
        if (graphics == IntPtr.Zero || _imagePlacements.Count == 0) return;

        _imageQueue.Clear();
        foreach (ImagePlacement placement in _imagePlacements)
        {
            if (placement.Layer == layer) _imageQueue.Add(placement);
        }
        if (_imageQueue.Count == 0) return;

        _imageQueue.Sort(static (a, b) =>
        {
            int order = a.Z.CompareTo(b.Z);
            return order != 0 ? order : a.ImageId.CompareTo(b.ImageId);
        });

        float scale = _rasterizationScale <= 0f ? 1f : _rasterizationScale;
        foreach (ImagePlacement placement in _imageQueue)
        {
            CanvasBitmap? bitmap = GetKittyTexture(resources, graphics, placement.ImageId,
                placement.Generation);
            if (bitmap is null) continue;

            float sourceX = MathF.Min(placement.SourceX, (float)bitmap.Size.Width);
            float sourceY = MathF.Min(placement.SourceY, (float)bitmap.Size.Height);
            float sourceWidth = MathF.Min(placement.SourceWidth, (float)bitmap.Size.Width - sourceX);
            float sourceHeight = MathF.Min(placement.SourceHeight, (float)bitmap.Size.Height - sourceY);
            if (sourceWidth <= 0f || sourceHeight <= 0f) continue;

            var source = new Rect(sourceX, sourceY, sourceWidth, sourceHeight);
            var destination = new Rect(
                placement.ViewportCol * cellW + placement.OffsetX / scale,
                placement.ViewportRow * cellH + placement.OffsetY / scale,
                placement.PixelWidth / scale,
                placement.PixelHeight / scale);
            if (destination.Width <= 0f || destination.Height <= 0f) continue;

            ds.DrawImage(bitmap, destination, source, 1f, CanvasImageInterpolation.Linear);
        }
    }

    /// <summary>
    /// Returns the uploaded texture for an image, creating it from the Kitty storage on first
    /// use and whenever the image's generation changes.
    /// </summary>
    private CanvasBitmap? GetKittyTexture(ICanvasResourceCreator resources, IntPtr graphics,
        uint imageId, ulong generation)
    {
        if (_imageTextures.TryGetValue(imageId, out ImageTexture? cached))
        {
            if (cached.Generation == generation)
            {
                cached.LastFrame = _imageFrame;
                return cached.Bitmap;
            }

            cached.Bitmap.Dispose();
            _imageTextures.Remove(imageId);
        }

        IntPtr image = GhosttyNative.ghostty_kitty_graphics_image(graphics, imageId);
        if (image == IntPtr.Zero) return null;

        uint width = 0, height = 0;
        int format = 0;
        // Since libghostty-vt 1.0 PIXELS is a GhosttyString { ptr, len }, not a bare
        // pointer. Reading it into an IntPtr would let the length field overwrite the
        // adjacent locals and turn a colour image into the gray branch below.
        var pixelData = default(GhosttyNative.GhosttyString);
        if (GhosttyNative.ghostty_kitty_graphics_image_get(
                image, GhosttyNative.KITTY_IMAGE_DATA_WIDTH, &width) != GhosttyNative.SUCCESS ||
            GhosttyNative.ghostty_kitty_graphics_image_get(
                image, GhosttyNative.KITTY_IMAGE_DATA_HEIGHT, &height) != GhosttyNative.SUCCESS ||
            GhosttyNative.ghostty_kitty_graphics_image_get(
                image, GhosttyNative.KITTY_IMAGE_DATA_FORMAT, &format) != GhosttyNative.SUCCESS ||
            GhosttyNative.ghostty_kitty_graphics_image_get(
                image, GhosttyNative.KITTY_IMAGE_DATA_PIXELS, &pixelData) != GhosttyNative.SUCCESS)
        {
            return null;
        }
        if (pixelData.Ptr == IntPtr.Zero || pixelData.Len == 0) return null;

        byte[]? pixels = PremultipliedBgra(
            width, height, format, pixelData.Ptr, pixelData.Len);
        if (pixels is null) return null;

        try
        {
            CanvasBitmap bitmap = CanvasBitmap.CreateFromBytes(resources, pixels,
                (int)width, (int)height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f,
                CanvasAlphaMode.Premultiplied);
            _imageTextures[imageId] = new ImageTexture
            {
                Bitmap = bitmap,
                Generation = generation,
                LastFrame = _imageFrame,
            };
            return bitmap;
        }
        catch (Exception error)
        {
            Diag.LogException("kitty.texture", error);
            return null;
        }
    }

    /// <summary>Converts one Kitty pixel format into premultiplied BGRA for Win2D.</summary>
    private static byte[]? PremultipliedBgra(uint width, uint height, int format,
        IntPtr data, nuint length)
    {
        if (width == 0 || height == 0 || data == IntPtr.Zero) return null;

        int channels = format switch
        {
            GhosttyNative.KITTY_IMAGE_FORMAT_RGB => 3,
            GhosttyNative.KITTY_IMAGE_FORMAT_RGBA => 4,
            GhosttyNative.KITTY_IMAGE_FORMAT_GRAY_ALPHA => 2,
            GhosttyNative.KITTY_IMAGE_FORMAT_GRAY => 1,
            _ => 0,
        };
        long pixels = (long)width * height;
        long sourceBytes = pixels * channels;
        // Stored images are always uncompressed; an unknown format code with a full
        // RGBA-sized buffer is treated as RGBA (seen on animation frames).
        if (channels == 0 && length == (nuint)(pixels * 4)) channels = 4;
        sourceBytes = pixels * channels;
        if (channels == 0 || pixels > int.MaxValue / 4 || sourceBytes > int.MaxValue ||
            length < (nuint)sourceBytes)
        {
            return null;
        }

        var source = new byte[(int)sourceBytes];
        Marshal.Copy(data, source, 0, source.Length);
        var output = new byte[(int)pixels * 4];
        for (long i = 0; i < pixels; i++)
        {
            long s = i * channels;
            long o = i * 4;
            byte r, g, b, a;
            switch (format)
            {
                case GhosttyNative.KITTY_IMAGE_FORMAT_RGBA:
                    r = source[s];
                    g = source[s + 1];
                    b = source[s + 2];
                    a = source[s + 3];
                    break;
                case GhosttyNative.KITTY_IMAGE_FORMAT_RGB:
                    r = source[s];
                    g = source[s + 1];
                    b = source[s + 2];
                    a = 255;
                    break;
                case GhosttyNative.KITTY_IMAGE_FORMAT_GRAY_ALPHA:
                    r = g = b = source[s];
                    a = source[s + 1];
                    break;
                default: // GRAY
                    r = g = b = source[s];
                    a = 255;
                    break;
            }

            if (a == 0)
            {
                output[o] = 0;
                output[o + 1] = 0;
                output[o + 2] = 0;
                output[o + 3] = 0;
            }
            else if (a == 255)
            {
                output[o] = b;
                output[o + 1] = g;
                output[o + 2] = r;
                output[o + 3] = 255;
            }
            else
            {
                output[o] = (byte)(b * a / 255);
                output[o + 1] = (byte)(g * a / 255);
                output[o + 2] = (byte)(r * a / 255);
                output[o + 3] = a;
            }
        }
        return output;
    }

    /// <summary>Drops textures that left the viewport, keeping recently used ones cached.</summary>
    private void PruneImages()
    {
        _imageFrame++;
        if (_imageTextures.Count == 0) return;

        List<uint>? stale = null;
        foreach (KeyValuePair<uint, ImageTexture> pair in _imageTextures)
        {
            if (_imageFrame - pair.Value.LastFrame > 120)
                (stale ??= new List<uint>()).Add(pair.Key);
        }

        if (stale is not null)
        {
            foreach (uint key in stale)
            {
                if (!_imageTextures.TryGetValue(key, out ImageTexture? texture)) continue;
                texture.Bitmap.Dispose();
                _imageTextures.Remove(key);
            }
        }

        // A burst of distinct images must not keep every texture alive; drop the least
        // recently used entries that this frame did not touch.
        while (_imageTextures.Count > 64)
        {
            uint oldestKey = 0;
            int oldestFrame = int.MaxValue;
            bool found = false;
            foreach (KeyValuePair<uint, ImageTexture> pair in _imageTextures)
            {
                // Never evict an image this frame drew; it would be re-uploaded immediately.
                if (pair.Value.LastFrame == _imageFrame) continue;
                if (pair.Value.LastFrame >= oldestFrame) continue;
                oldestFrame = pair.Value.LastFrame;
                oldestKey = pair.Key;
                found = true;
            }
            if (!found) break;

            ImageTexture oldest = _imageTextures[oldestKey];
            oldest.Bitmap.Dispose();
            _imageTextures.Remove(oldestKey);
        }
    }

    /// <summary>Releases the image iterator and every uploaded Kitty texture.</summary>
    public void DisposeImages()
    {
        if (_imageIterator != IntPtr.Zero)
        {
            GhosttyNative.ghostty_render_state_image_iterator_free(_imageIterator);
            _imageIterator = IntPtr.Zero;
        }

        foreach (ImageTexture texture in _imageTextures.Values) texture.Bitmap.Dispose();
        _imageTextures.Clear();
        _imagePlacements.Clear();
    }

    /// <summary>
    /// Releases everything this renderer owns. The shared <c>CanvasDevice</c> belongs to the
    /// process and stays alive; everything else — fonts, formats, typography, cached layouts,
    /// background image, shaders and uploaded textures — is per pane and dies with it. The
    /// pane calls this when it closes: without it these wrappers would only be released by
    /// their finalizers, and the managed heap is far too small to make the GC run often.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeImages();
        DisposeShaders();
        InvalidateLayouts();
        _segments.Clear();
        _coverage.Clear();
        _faces.Clear();
        _scales.Clear();
        _iconScales.Clear();
        _familyExists.Clear();
        _contrast.Clear();

        foreach (CanvasTextFormat format in _formats.Values) format.Dispose();
        _formats.Clear();
        _baseFormat.Dispose();
        _typography?.Dispose();
        _typography = null;
        // The system font set is process-wide (FontCache); a pane dying must not release it.
        _backgroundImage?.Dispose();
        _backgroundImage = null;
        _backgroundImagePath = string.Empty;
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
        // A load started before the pane closed can finish afterwards.
        if (_disposed || !string.Equals(path, _backgroundImagePath, StringComparison.Ordinal))
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
        if (style.Faint)
        {
            float opacity = _config?.FaintOpacity ?? 0.5f;
            return Blend(fg, bg, 1.0f - Math.Clamp(opacity, 0f, 1f));
        }

        if (style.Bold && !explicitColor && _config is { HasBoldColor: true })
        {
            Rgb rgb = _config.BoldColor;
            return Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
        }

        return fg;
    }

    /// <summary>True when the column is in a search span; a selected match wins over others.</summary>
    private static bool FindSearchSpan(List<SearchSpan> spans, int col, out bool selected)
    {
        selected = false;
        bool found = false;
        foreach (SearchSpan span in spans)
        {
            if (col < span.StartX || col > span.EndX) continue;
            if (!span.Selected)
            {
                found = true;
                continue;
            }
            selected = true;
            return true;
        }
        return found;
    }

    private Color SearchBackgroundColor(bool selected, Color cellBg)
    {
        SearchColor setting = default;
        if (_config is not null)
            setting = selected ? _config.SearchSelectedBackground : _config.SearchBackground;

        Color fallback = selected
            ? Color.FromArgb(255, 0xF2, 0xA5, 0x7E)
            : Color.FromArgb(255, 0xFF, 0xE0, 0x82);
        return ResolveSearchColor(setting, cellBg, cellBg, fallback);
    }

    private Color SearchForegroundColor(bool selected, Color cellFg, Color cellBg)
    {
        SearchColor setting = default;
        if (_config is not null)
            setting = selected ? _config.SearchSelectedForeground : _config.SearchForeground;

        return ResolveSearchColor(setting, cellFg, cellBg, Color.FromArgb(255, 0, 0, 0));
    }

    private static Color ResolveSearchColor(SearchColor setting, Color cellFg, Color cellBg, Color fallback)
    {
        return setting.Kind switch
        {
            SearchColorKind.Explicit => Color.FromArgb(255, setting.Value.R, setting.Value.G, setting.Value.B),
            SearchColorKind.CellForeground => cellFg,
            SearchColorKind.CellBackground => cellBg,
            _ => fallback,
        };
    }

    private Color SelectionFillColor()
    {
        if (_config is { HasSelectionBackground: true })
        {
            Rgb rgb = _config.SelectionBackground;
            return Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
        }

        // A bundled theme can carry its own selection color; the design palettes fall back
        // to the translucent veil below.
        if (Theme.Terminal is { Custom: true, SelectionBackgroundSet: true } terminal)
        {
            Rgb rgb = terminal.SelectionBackground;
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
            DrawTextRun(ds, grapheme, x, rowTop, fg, false, false, FamilyForGrapheme(grapheme, 0));
            ds.FillRectangle(new Rect(x, rowTop + cellH - underline - 2, cells * cellW, underline), fg);

            col += cells;
        }

        return col;
    }

    /// <summary>
    /// Draws a single-width run split by fallback family. Every segment starts on the cell its
    /// first grapheme occupies, so a fallback font's advance cannot shift the rest of the run.
    /// </summary>
    private void DrawSegmentRun(CanvasDrawingSession ds, Run run, RunSegment[] segments,
        int split, int splitCell, float cellW, float textY)
    {
        foreach (RunSegment segment in segments)
        {
            int end = segment.Index + segment.Length;

            // The cursor falls inside this segment, so it is drawn as two layouts: the
            // shaper must not join the cells on either side of it.
            if (split > segment.Index && split < end)
            {
                DrawTextRun(ds, run.Text.Substring(segment.Index, split - segment.Index),
                    (run.Col + segment.Cell) * cellW, textY, run.Fg, run.Bold, run.Italic, segment.Family);
                DrawTextRun(ds, run.Text.Substring(split, end - split),
                    (run.Col + splitCell) * cellW, textY, run.Fg, run.Bold, run.Italic, segment.Family);
                continue;
            }

            DrawTextRun(ds, run.Text.Substring(segment.Index, segment.Length),
                (run.Col + segment.Cell) * cellW, textY, run.Fg, run.Bold, run.Italic, segment.Family);
        }
    }

    /// <summary>
    /// Draws a run that contains double-width glyphs, placing every grapheme on its own cell
    /// so a fallback font's advance cannot push the rest of the line off the grid. Each
    /// grapheme moves by the width the library gave its column; the table here is only the
    /// answer when that column is not in the run's record.
    /// </summary>
    private void DrawWideRun(CanvasDrawingSession ds, Run run, byte[]? widths, float cellW, float rowTop)
    {
        int col = run.Col;
        int face = (run.Bold ? 1 : 0) | (run.Italic ? 2 : 0);

        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(run.Text);
        while (elements.MoveNext())
        {
            string grapheme = elements.GetTextElement();
            if (grapheme.Length == 0) continue;

            DrawTextRun(ds, grapheme, col * cellW, rowTop, run.Fg, run.Bold, run.Italic,
                FamilyForGrapheme(grapheme, face));

            int advance = widths is not null && col >= 0 && col < widths.Length ? widths[col] : 0;
            col += advance > 0 ? advance : WideText.IsWide(grapheme) ? 2 : 1;
        }
    }

    /// <summary>Whether a cell's text is plain ASCII, which is never double width.</summary>
    private static bool IsAsciiCell(string text)
    {
        foreach (char ch in text)
        {
            if (ch >= 0x80) return false;
        }
        return true;
    }

    /// <summary>
    /// The library's width for one render-state cell: narrow, wide, or one half of a wide
    /// glyph. Returns -1 when it cannot answer, so the caller falls back to its own table.
    /// </summary>
    private static int CellWide(IntPtr cells)
    {
        ulong raw = 0;
        if (GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_RAW, &raw) != GhosttyNative.SUCCESS)
            return -1;

        int kind = -1;
        if (GhosttyNative.ghostty_cell_get(raw, GhosttyNative.CD_WIDE, &kind) != GhosttyNative.SUCCESS)
            return -1;

        return kind;
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
