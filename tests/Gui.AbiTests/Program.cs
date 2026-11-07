using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Gui.Term;

namespace Gui.AbiTests;

/// <summary>
/// ABI contract test for Gui/Gui/Term/GhosttyNative.cs.
///
/// libghostty-vt exports ghostty_type_json(): a machine-readable manifest of every C type's
/// size, alignment and field offsets for the exact DLL that was built. This program compiles
/// the real P/Invoke declarations and verifies them against that manifest, so a struct that
/// drifts from the native ABI (for example GhosttyPoint.Y declared as ushort at offset 10
/// instead of u32 at offset 12) fails instead of silently corrupting terminal selection.
///
/// Run:     dotnet run --project tests/Gui.AbiTests -c Release
/// DLL:     GUI_GHOSTTY_DLL=&lt;path&gt; (defaults to Gui/Gui/native/x64/ghostty-vt.dll)
/// Exit:    0 = all checks passed, 1 = ABI mismatch, 2 = could not run the test.
/// </summary>
internal static class Program
{
    private const string DllName = "ghostty-vt.dll";

    private static int _checks;
    private static int _failures;
    private static JsonElement _types;
    private static string _dllPath = string.Empty;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr TypeJsonFn();

    private sealed record FieldMap(string Clr, string AbiPath);
    private sealed record StructMap(Type Clr, string Abi, FieldMap[] Fields, string[] Ignored)
    {
        public StructMap(Type clr, string abi, params FieldMap[] fields)
            : this(clr, abi, fields, Array.Empty<string>())
        {
        }
    }

    private sealed record ConstMap(string Clr, string AbiEnum, string Value);

    private static int Main()
    {
        try
        {
            _dllPath = FindDll();
            // The declarations are compiled into this assembly, so the resolver has to be
            // installed before the first P/Invoke the smoke test makes.
            NativeLibrary.SetDllImportResolver(typeof(GhosttyNative).Assembly, ImportResolver);

            using JsonDocument manifest = LoadManifest();
            JsonElement root = manifest.RootElement;
            _types = root.GetProperty("types");

            CheckAbi(root.GetProperty("abi"));
            CheckStructs();
            CheckConstants();
            CheckCoverage();
            CheckScrollbackSmoke();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("[fatal] " + error);
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0
            ? $"ABI contract: {_checks} checks passed."
            : $"ABI contract: {_failures} of {_checks} checks FAILED.");
        return _failures == 0 ? 0 : 1;
    }

    // ---------------- loading ----------------

    private static JsonDocument LoadManifest()
    {
        Console.WriteLine($"DLL: {_dllPath}");

        IntPtr handle = NativeLibrary.Load(_dllPath);
        IntPtr export = NativeLibrary.GetExport(handle, "ghostty_type_json");
        TypeJsonFn fn = Marshal.GetDelegateForFunctionPointer<TypeJsonFn>(export);
        IntPtr json = fn();
        if (json == IntPtr.Zero) throw new InvalidOperationException("ghostty_type_json returned NULL");

        // The pointer is owned by the DLL and valid for the process lifetime; do not free it.
        string text = Marshal.PtrToStringUTF8(json)
            ?? throw new InvalidOperationException("ghostty_type_json returned invalid UTF-8");
        return JsonDocument.Parse(text);
    }

    /// <summary>
    /// Binds the P/Invoke declarations to the same DLL the manifest came from, so the smoke
    /// checks exercise that binary even when nothing named ghostty-vt.dll sits next to the
    /// test executable.
    /// </summary>
    private static IntPtr ImportResolver(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == DllName ? NativeLibrary.Load(_dllPath) : IntPtr.Zero;

    private static string FindDll()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("GUI_GHOSTTY_DLL");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            if (!File.Exists(fromEnv)) throw new FileNotFoundException("GUI_GHOSTTY_DLL does not exist", fromEnv);
            return fromEnv;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string sourceTree = Path.Combine(dir.FullName, "Gui", "Gui", "native", "x64", DllName);
            if (File.Exists(sourceTree)) return sourceTree;

            string besideExe = Path.Combine(dir.FullName, DllName);
            if (File.Exists(besideExe)) return besideExe;
        }

        throw new FileNotFoundException(
            $"Could not locate {DllName}. Set GUI_GHOSTTY_DLL to the full path.");
    }

    // ---------------- checks ----------------

    private static void CheckAbi(JsonElement abi)
    {
        Section("ABI header");
        Equal("pointer_size", IntPtr.Size, abi.GetProperty("pointer_size").GetInt32());
        Equal("usize_size", Marshal.SizeOf<nuint>(), abi.GetProperty("usize_size").GetInt32());
        Check("target is x86_64", abi.GetProperty("target").GetString() == "x86_64");
        Check("os is windows", abi.GetProperty("os").GetString() == "windows");
        Check("test process is 64-bit", IntPtr.Size == 8);
    }

    /// <summary>
    /// Each entry maps a C# struct to its manifest type and each C# field to a manifest
    /// field path. Paths descend through nested structs/unions ("value.coordinate.y"), and
    /// "bits[2]" addresses an array element. Offsets and widths are resolved from the DLL,
    /// so updating the DLL makes this test detect drift rather than hiding it.
    /// </summary>
    private static readonly StructMap[] Structs =
    {
        new(typeof(GhosttyNative.GhosttyString), "GhosttyString",
            new FieldMap("Ptr", "ptr"), new FieldMap("Len", "len")),
        new(typeof(GhosttyNative.GhosttyBuffer), "GhosttyBuffer",
            new FieldMap("Ptr", "ptr"), new FieldMap("Cap", "cap"), new FieldMap("Len", "len")),
        new(typeof(GhosttyNative.GhosttyStyleColor), "GhosttyStyleColor",
            new FieldMap("Tag", "tag"), new FieldMap("Value", "value")),
        new(typeof(GhosttyNative.GhosttyStyle), "GhosttyStyle",
            new FieldMap("Size", "size"),
            new FieldMap("FgColor", "fg_color"), new FieldMap("BgColor", "bg_color"),
            new FieldMap("UnderlineColor", "underline_color"),
            new FieldMap("Bold", "bold"), new FieldMap("Italic", "italic"),
            new FieldMap("Faint", "faint"), new FieldMap("Blink", "blink"),
            new FieldMap("Inverse", "inverse"), new FieldMap("Invisible", "invisible"),
            new FieldMap("Strikethrough", "strikethrough"), new FieldMap("Overline", "overline"),
            new FieldMap("Underline", "underline")),
        new(typeof(GhosttyNative.GhosttyTerminalScrollViewport), "GhosttyTerminalScrollViewport",
            new FieldMap("Tag", "tag"), new FieldMap("Delta", "value.delta")),
        new(typeof(GhosttyNative.GhosttyTerminalScrollbar), "GhosttyTerminalScrollbar",
            new FieldMap("Total", "total"), new FieldMap("Offset", "offset"),
            new FieldMap("Len", "len")),
        new(typeof(GhosttyNative.GhosttyGridRef), "GhosttyGridRef",
            new FieldMap("Size", "size"), new FieldMap("Node", "node"),
            new FieldMap("X", "x"), new FieldMap("Y", "y")),
        new(typeof(GhosttyNative.GhosttyPoint), "GhosttyPoint",
            new FieldMap("Tag", "tag"),
            new FieldMap("X", "value.coordinate.x"), new FieldMap("Y", "value.coordinate.y")),
        new(typeof(GhosttyNative.GhosttySelection), "GhosttySelection",
            new FieldMap("Size", "size"), new FieldMap("Start", "start"),
            new FieldMap("End", "end"), new FieldMap("Rectangle", "rectangle")),
        new(typeof(GhosttyNative.GhosttyTerminalModeConfig), "GhosttyTerminalModeConfig",
            new FieldMap("Mode", "mode"), new FieldMap("Value", "value")),
        new(typeof(GhosttyNative.GhosttyRenderStateRowSelection), "GhosttyRenderStateRowSelection",
            new FieldMap("Size", "size"), new FieldMap("StartX", "start_x"),
            new FieldMap("EndX", "end_x")),
        new(typeof(GhosttyNative.GhosttyRenderStateCursor), "GhosttyRenderStateCursor",
            new FieldMap("Size", "size"),
            new FieldMap("ViewportHasValue", "viewport_has_value"),
            new FieldMap("ViewportX", "viewport_x"), new FieldMap("ViewportY", "viewport_y"),
            new FieldMap("WideTail", "wide_tail"), new FieldMap("Visible", "visible"),
            new FieldMap("Blinking", "blinking"), new FieldMap("PasswordInput", "password_input"),
            new FieldMap("VisualStyle", "visual_style")),
        // The C# declaration leaves the trailing screen-extra bytes zeroed via Size = 32
        // instead of declaring GhosttyFormatterScreenExtra; that omission is intentional.
        new(typeof(GhosttyNative.GhosttyFormatterTerminalExtra), "GhosttyFormatterTerminalExtra",
            new[]
            {
                new FieldMap("Size", "size"), new FieldMap("Palette", "palette"),
                new FieldMap("Modes", "modes"), new FieldMap("ScrollingRegion", "scrolling_region"),
                new FieldMap("Tabstops", "tabstops"), new FieldMap("Pwd", "pwd"),
                new FieldMap("Keyboard", "keyboard"),
            },
            new[] { "screen" }),
        new(typeof(GhosttyNative.GhosttyFormatterTerminalOptions), "GhosttyFormatterTerminalOptions",
            new FieldMap("Size", "size"), new FieldMap("Emit", "emit"),
            new FieldMap("Unwrap", "unwrap"), new FieldMap("Trim", "trim"),
            new FieldMap("Extra", "extra"), new FieldMap("Selection", "selection")),
        // The C ABI declares bits[4]; the C# side spells the four words out.
        new(typeof(GhosttyNative.ColorPaletteMask), "GhosttyColorPaletteMask",
            new FieldMap("Bits0", "bits[0]"), new FieldMap("Bits1", "bits[1]"),
            new FieldMap("Bits2", "bits[2]"), new FieldMap("Bits3", "bits[3]")),
        new(typeof(Rgb), "GhosttyColorRgb",
            new FieldMap("R", "r"), new FieldMap("G", "g"), new FieldMap("B", "b")),
    };

    private static void CheckStructs()
    {
        Section("struct layouts");

        foreach (StructMap map in Structs)
        {
            JsonElement type = _types.GetProperty(map.Abi);
            string label = $"{map.Clr.Name} -> {map.Abi}";

            Equal($"{label} size", type.GetProperty("size").GetInt32(), Marshal.SizeOf(map.Clr));

            foreach (FieldMap field in map.Fields)
            {
                (int abiOffset, int abiSize) = Resolve(map.Abi, field.AbiPath);
                FieldInfo? info = map.Clr.GetField(field.Clr,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (info is null)
                {
                    Check($"{label}.{field.Clr} exists", false);
                    continue;
                }

                Equal($"{label}.{field.Clr} offset", abiOffset,
                    Marshal.OffsetOf(map.Clr, field.Clr).ToInt32());
                Equal($"{label}.{field.Clr} width", abiSize, Marshal.SizeOf(info.FieldType));
            }

            // Every C# field must be covered, and every top-level ABI field must either be
            // covered by a path or explicitly declared as intentionally omitted.
            FieldInfo[] clrFields = map.Clr.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            HashSet<string> mapped = map.Fields.Select(f => f.Clr).ToHashSet(StringComparer.Ordinal);
            foreach (FieldInfo field in clrFields)
                Check($"{label} covers {field.Name}", mapped.Contains(field.Name));

            HashSet<string> roots = map.Fields
                .Select(f => f.AbiPath.Split('.')[0].Split('[')[0])
                .Concat(map.Ignored)
                .ToHashSet(StringComparer.Ordinal);
            foreach (JsonProperty abiField in type.GetProperty("fields").EnumerateObject())
                Check($"{label} accounts for {abiField.Name}", roots.Contains(abiField.Name));
        }
    }

    /// <summary>
    /// Resolves a dotted / indexed path into (absolute byte offset, byte width) using only
    /// the DLL manifest. The manifest is the authority; nothing here hardcodes an offset.
    /// </summary>
    private static (int Offset, int Size) Resolve(string abiType, string path)
    {
        int offset = 0;
        JsonElement current = _types.GetProperty(abiType);
        string[] parts = path.Split('.');

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            int index = -1;
            int bracket = part.IndexOf('[');
            if (bracket >= 0)
            {
                index = int.Parse(part.AsSpan(bracket + 1, part.Length - bracket - 2),
                    CultureInfo.InvariantCulture);
                part = part[..bracket];
            }

            if (!current.TryGetProperty("fields", out JsonElement fields) ||
                !fields.TryGetProperty(part, out JsonElement field))
                throw new InvalidOperationException($"ABI field '{part}' missing while resolving '{abiType}:{path}'");

            offset += field.GetProperty("offset").GetInt32();
            int size = field.GetProperty("size").GetInt32();
            string type = field.GetProperty("type").GetString()!;

            if (bracket >= 0)
            {
                if (type != "array")
                    throw new InvalidOperationException($"ABI field '{part}' is not an array in '{path}'");
                int elemSize = PrimitiveSize(field.GetProperty("elem").GetString()!);
                return (offset + index * elemSize, elemSize);
            }

            if (i == parts.Length - 1) return (offset, size);

            if (!_types.TryGetProperty(type, out JsonElement next))
                throw new InvalidOperationException($"ABI type '{type}' missing while resolving '{path}'");
            current = next;
        }

        throw new InvalidOperationException($"Empty ABI path for '{abiType}'");
    }

    private static int PrimitiveSize(string name) => name switch
    {
        "u8" or "i8" or "bool" => 1,
        "u16" or "i16" => 2,
        "u32" or "i32" or "f32" => 4,
        "u64" or "i64" or "f64" => 8,
        "pointer" => IntPtr.Size,
        _ => throw new InvalidOperationException($"Unknown ABI primitive '{name}'"),
    };

    /// <summary>Constants are checked against the manifest enums, not against literals.</summary>
    private static readonly ConstMap[] Constants =
    {
        new("SUCCESS", "GhosttyResult", "SUCCESS"),
        new("OUT_OF_SPACE", "GhosttyResult", "OUT_OF_SPACE"),

        new("OPT_USERDATA", "GhosttyTerminalOption", "USERDATA"),
        new("OPT_WRITE_PTY", "GhosttyTerminalOption", "WRITE_PTY"),
        new("OPT_BELL", "GhosttyTerminalOption", "BELL"),
        new("OPT_TITLE_CHANGED", "GhosttyTerminalOption", "TITLE_CHANGED"),
        new("OPT_COLOR_FOREGROUND", "GhosttyTerminalOption", "COLOR_FOREGROUND"),
        new("OPT_COLOR_BACKGROUND", "GhosttyTerminalOption", "COLOR_BACKGROUND"),
        new("OPT_COLOR_CURSOR", "GhosttyTerminalOption", "COLOR_CURSOR"),
        new("OPT_COLOR_PALETTE", "GhosttyTerminalOption", "COLOR_PALETTE"),
        new("OPT_SELECTION", "GhosttyTerminalOption", "SELECTION"),
        new("OPT_PWD_CHANGED", "GhosttyTerminalOption", "PWD_CHANGED"),
        new("OPT_SCROLLBACK_MAX_BYTES", "GhosttyTerminalOption", "SCROLLBACK_MAX_BYTES"),
        new("OPT_SCROLLBACK_MAX_LINES", "GhosttyTerminalOption", "SCROLLBACK_MAX_LINES"),
        new("OPT_RESIZE_WINDOW", "GhosttyTerminalOption", "RESIZE_WINDOW"),

        new("TDATA_COLS", "GhosttyTerminalData", "COLS"),
        new("TDATA_ROWS", "GhosttyTerminalData", "ROWS"),
        new("TDATA_SCROLLBAR", "GhosttyTerminalData", "SCROLLBAR"),
        new("TDATA_TITLE", "GhosttyTerminalData", "TITLE"),
        new("TDATA_PWD", "GhosttyTerminalData", "PWD"),
        new("TDATA_SELECTION", "GhosttyTerminalData", "SELECTION"),
        new("TDATA_MODE", "GhosttyTerminalData", "MODE"),

        new("RS_COLS", "GhosttyRenderStateData", "COLS"),
        new("RS_ROWS", "GhosttyRenderStateData", "ROWS"),
        new("RS_DIRTY", "GhosttyRenderStateData", "DIRTY"),
        new("RS_ROW_ITERATOR", "GhosttyRenderStateData", "ROW_ITERATOR"),
        new("RS_COLOR_BACKGROUND", "GhosttyRenderStateData", "COLOR_BACKGROUND"),
        new("RS_COLOR_FOREGROUND", "GhosttyRenderStateData", "COLOR_FOREGROUND"),
        new("RS_COLOR_CURSOR", "GhosttyRenderStateData", "COLOR_CURSOR"),
        new("RS_COLOR_CURSOR_HAS_VALUE", "GhosttyRenderStateData", "COLOR_CURSOR_HAS_VALUE"),
        new("RS_CURSOR_VISUAL_STYLE", "GhosttyRenderStateData", "CURSOR_VISUAL_STYLE"),
        new("RS_CURSOR_VISIBLE", "GhosttyRenderStateData", "CURSOR_VISIBLE"),
        new("RS_CURSOR_BLINKING", "GhosttyRenderStateData", "CURSOR_BLINKING"),
        new("RS_CURSOR_VIEWPORT_HAS_VALUE", "GhosttyRenderStateData", "CURSOR_VIEWPORT_HAS_VALUE"),
        new("RS_CURSOR_VIEWPORT_X", "GhosttyRenderStateData", "CURSOR_VIEWPORT_X"),
        new("RS_CURSOR_VIEWPORT_Y", "GhosttyRenderStateData", "CURSOR_VIEWPORT_Y"),
        new("RS_CURSOR_VIEWPORT_WIDE_TAIL", "GhosttyRenderStateData", "CURSOR_VIEWPORT_WIDE_TAIL"),
        new("RS_CURSOR", "GhosttyRenderStateData", "CURSOR"),
        new("RS_COLORS", "GhosttyRenderStateData", "COLORS"),

        new("ROW_DATA_CELLS", "GhosttyRenderStateRowData", "CELLS"),
        new("ROW_DATA_SELECTION", "GhosttyRenderStateRowData", "SELECTION"),
        new("ROW_VIEWPORT_Y", "GhosttyRenderStateRowData", "VIEWPORT_Y"),

        new("CELL_RAW", "GhosttyRenderStateRowCellsData", "RAW"),
        new("CELL_STYLE", "GhosttyRenderStateRowCellsData", "STYLE"),
        new("CELL_GRAPHEMES_LEN", "GhosttyRenderStateRowCellsData", "GRAPHEMES_LEN"),
        new("CELL_BG_COLOR", "GhosttyRenderStateRowCellsData", "BG_COLOR"),
        new("CELL_FG_COLOR", "GhosttyRenderStateRowCellsData", "FG_COLOR"),
        new("CELL_SELECTED", "GhosttyRenderStateRowCellsData", "SELECTED"),
        new("CELL_HAS_STYLING", "GhosttyRenderStateRowCellsData", "HAS_STYLING"),
        new("CELL_GRAPHEMES_UTF8", "GhosttyRenderStateRowCellsData", "GRAPHEMES_UTF8"),

        new("CD_WIDE", "GhosttyCellData", "WIDE"),

        new("WIDE_NARROW", "GhosttyCellWide", "NARROW"),
        new("WIDE_WIDE", "GhosttyCellWide", "WIDE"),
        new("WIDE_SPACER_TAIL", "GhosttyCellWide", "SPACER_TAIL"),
        new("WIDE_SPACER_HEAD", "GhosttyCellWide", "SPACER_HEAD"),

        new("SCROLL_TOP", "GhosttyTerminalScrollViewportTag", "TOP"),
        new("SCROLL_BOTTOM", "GhosttyTerminalScrollViewportTag", "BOTTOM"),
        new("SCROLL_DELTA", "GhosttyTerminalScrollViewportTag", "DELTA"),

        new("POINT_VIEWPORT", "GhosttyPointTag", "VIEWPORT"),
        new("KEY_ACTION_PRESS", "GhosttyKeyAction", "PRESS"),
        new("FORMAT_PLAIN", "GhosttyFormatterFormat", "PLAIN"),

        new("CURSOR_BAR", "GhosttyRenderStateCursorVisualStyle", "BAR"),
        new("CURSOR_BLOCK", "GhosttyRenderStateCursorVisualStyle", "BLOCK"),
        new("CURSOR_UNDERLINE", "GhosttyRenderStateCursorVisualStyle", "UNDERLINE"),
        new("CURSOR_BLOCK_HOLLOW", "GhosttyRenderStateCursorVisualStyle", "BLOCK_HOLLOW"),
    };

    /// <summary>GhosttyMods is a u16 bitmask alias, not an enum, so it has no manifest values.</summary>
    private static readonly string[] UnmappedConstants =
    {
        "MODS_SHIFT", "MODS_CTRL", "MODS_ALT", "MODS_SUPER", "MODS_CAPS_LOCK", "MODS_NUM_LOCK",
    };

    private static void CheckConstants()
    {
        Section("option / enum constants");

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (ConstMap map in Constants)
        {
            object? value = typeof(GhosttyNative).GetField(map.Clr, flags)?.GetRawConstantValue();
            int expected = _types.GetProperty(map.AbiEnum)
                .GetProperty("values").GetProperty(map.Value).GetInt32();
            Equal($"{map.Clr} == {map.AbiEnum}.{map.Value}", expected,
                value is null ? int.MinValue : Convert.ToInt32(value, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// New structs or constants added to GhosttyNative.cs must come with a contract, otherwise
    /// they would silently escape the checks above.
    /// </summary>
    private static void CheckCoverage()
    {
        Section("declaration coverage");

        HashSet<Type> coveredStructs = Structs.Select(s => s.Clr).ToHashSet();
        foreach (Type nested in typeof(GhosttyNative)
            .GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Where(t => t.IsValueType && !t.IsEnum))
            Check($"struct {nested.Name} has a contract", coveredStructs.Contains(nested));

        HashSet<string> coveredConstants = Constants.Select(c => c.Clr)
            .Concat(UnmappedConstants).ToHashSet(StringComparer.Ordinal);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (FieldInfo field in typeof(GhosttyNative).GetFields(flags)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int)))
            Check($"const {field.Name} has a contract", coveredConstants.Contains(field.Name));
    }

    // ---------------- scrollback smoke ----------------

    /// <summary>Lines of output the smoke checks push through a terminal.</summary>
    private const int LineCount = 5_000;

    /// <summary>
    /// Rows the LF-only run occupies, recorded from the bundled DLL. A bare LF moves the
    /// cursor down without resetting the column, so those lines wrap and take more rows than
    /// there are lines; the number catches a fixture that quietly turns into a CRLF run.
    /// </summary>
    private const ulong LfOnlyRows = 5_486;

    /// <summary>
    /// Behavioural check for the two scrollback caps. The sections above only prove that the
    /// declarations match the manifest; this one drives a real terminal through the same
    /// P/Invoke surface the app uses, so a wrong option tag or a mis-shaped call fails here
    /// instead of quietly leaving the app's scrollback uncapped.
    /// </summary>
    private static void CheckScrollbackSmoke()
    {
        Section("scrollback smoke");

        ulong capped = ScrollbackRows(1_000, "\r\n");
        Check("row cap trims the history", capped < LineCount, $"kept {capped} of {LineCount}");
        Check("row cap keeps what it allows", capped > 100 && capped <= 1_000, $"kept {capped}");

        ulong unlimited = ScrollbackRows(ulong.MaxValue, "\r\n");
        Check("unlimited keeps every row", unlimited == LineCount + 1, $"kept {unlimited}");

        // Regression for line-ending handling: a bare LF moves the cursor down but leaves the
        // column where it was, so the same lines wrap and occupy more rows. A helper that
        // splits the stream on '\n' and counts entries as rows would miss all of these.
        ulong lfOnly = ScrollbackRows(ulong.MaxValue, "\n");
        Check("lf-only output wraps into more rows", lfOnly > LineCount + 1, $"kept {lfOnly}");
        Check("lf-only keeps its recorded row count", lfOnly == LfOnlyRows,
            $"kept {lfOnly}, expected {LfOnlyRows}");
    }

    /// <summary>
    /// Feeds <see cref="LineCount"/> lines through a fresh terminal and reports how many rows
    /// it kept. The byte cap is lifted so only the row cap is measured.
    /// </summary>
    private static unsafe ulong ScrollbackRows(ulong lineCap, string lineEnding)
    {
        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out IntPtr terminal, 80, 24) != GhosttyNative.SUCCESS)
            throw new InvalidOperationException("ghostty_terminal_new failed");

        try
        {
            nuint bytes = 50_000_000;
            GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_SCROLLBACK_MAX_BYTES, &bytes);
            nuint lines = (nuint)lineCap;
            GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_SCROLLBACK_MAX_LINES, &lines);

            var text = new StringBuilder(64 * LineCount);
            for (int i = 0; i < LineCount; i++) text.Append("row ").Append(i).Append(lineEnding);
            byte[] payload = Encoding.ASCII.GetBytes(text.ToString());
            fixed (byte* pointer = payload)
                GhosttyNative.ghostty_terminal_vt_write(terminal, pointer, (nuint)payload.Length);

            GhosttyNative.GhosttyTerminalScrollbar state;
            if (GhosttyNative.ghostty_terminal_get(terminal, GhosttyNative.TDATA_SCROLLBAR, &state) != GhosttyNative.SUCCESS)
                throw new InvalidOperationException("ghostty_terminal_get(SCROLLBAR) failed");
            return state.Total;
        }
        finally
        {
            GhosttyNative.ghostty_terminal_free(terminal);
        }
    }

    // ---------------- reporting ----------------

    private static void Section(string name) => Console.WriteLine($"{name}:");

    private static void Equal(string what, int expected, int actual) =>
        Check(what, expected == actual, $"expected {expected}, got {actual}");

    private static void Check(string what, bool ok, string detail = "")
    {
        _checks++;
        if (ok)
        {
            Console.WriteLine($"  ok   {what}");
        }
        else
        {
            _failures++;
            Console.WriteLine($"  FAIL {what} {detail}");
        }
    }
}
