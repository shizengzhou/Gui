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
            CheckMouseSmoke();
            CheckBellSmoke();
            CheckTitleReportSmoke();
            CheckEnquirySmoke();
            CheckResizeWindowSmoke();
            CheckXtChecksumSmoke();
            CheckDesktopNotificationSmoke();
            CheckSearchSmoke();
            CheckSnapshotSmoke();
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
        new(typeof(GhosttyNative.GhosttyClipboardContent), "GhosttyClipboardContent",
            new FieldMap("Mime", "mime"), new FieldMap("Data", "data")),
        new(typeof(GhosttyNative.GhosttyClipboardWrite), "GhosttyClipboardWrite",
            new FieldMap("Size", "size"), new FieldMap("Location", "location"),
            new FieldMap("Contents", "contents"), new FieldMap("ContentsLen", "contents_len"),
            new FieldMap("Name", "name"), new FieldMap("Granted", "granted"),
            new FieldMap("CanRemember", "can_remember"), new FieldMap("Ctx", "ctx"),
            new FieldMap("Reply", "reply")),
        new(typeof(GhosttyNative.GhosttyClipboardRead), "GhosttyClipboardRead",
            new FieldMap("Size", "size"), new FieldMap("Location", "location"),
            new FieldMap("Mimes", "mimes"), new FieldMap("MimesLen", "mimes_len"),
            new FieldMap("List", "list"), new FieldMap("Name", "name"),
            new FieldMap("Granted", "granted"), new FieldMap("CanRemember", "can_remember"),
            new FieldMap("Ctx", "ctx"), new FieldMap("Reply", "reply")),
        new(typeof(GhosttyNative.GhosttyClipboardWriteReply), "GhosttyClipboardWriteReply",
            new FieldMap("Size", "size"), new FieldMap("Result", "result"),
            new FieldMap("Remember", "remember")),
        new(typeof(GhosttyNative.GhosttyClipboardReadReply), "GhosttyClipboardReadReply",
            new FieldMap("Size", "size"), new FieldMap("Result", "result"),
            new FieldMap("Contents", "contents"), new FieldMap("ContentsLen", "contents_len"),
            new FieldMap("Available", "available"), new FieldMap("AvailableLen", "available_len"),
            new FieldMap("Remember", "remember")),
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
        new(typeof(GhosttyNative.GhosttyMousePosition), "GhosttyMousePosition",
            new FieldMap("X", "x"), new FieldMap("Y", "y")),
        new(typeof(GhosttyNative.GhosttySelectionBuffer), "GhosttySelectionBuffer",
            new FieldMap("Ptr", "ptr"), new FieldMap("Cap", "cap"), new FieldMap("Len", "len")),
        new(typeof(GhosttyNative.GhosttyPointCoordinate), "GhosttyPointCoordinate",
            new FieldMap("X", "x"), new FieldMap("Y", "y")),
        new(typeof(GhosttyNative.GhosttyPoint), "GhosttyPoint",
            new FieldMap("Tag", "tag"),
            new FieldMap("X", "value.coordinate.x"), new FieldMap("Y", "value.coordinate.y")),
        new(typeof(GhosttyNative.GhosttySelection), "GhosttySelection",
            new FieldMap("Size", "size"), new FieldMap("Start", "start"),
            new FieldMap("End", "end"), new FieldMap("Rectangle", "rectangle")),
        new(typeof(GhosttyNative.GhosttyTerminalModeConfig), "GhosttyTerminalModeConfig",
            new FieldMap("Mode", "mode"), new FieldMap("Value", "value")),
        new(typeof(GhosttyNative.GhosttyTerminalProgramStatus), "GhosttyTerminalProgramStatus",
            new FieldMap("Size", "size"), new FieldMap("State", "state"),
            new FieldMap("Kind", "kind"), new FieldMap("Progress", "progress"),
            new FieldMap("Id", "id"), new FieldMap("App", "app"),
            new FieldMap("Title", "title"), new FieldMap("Message", "message")),
        new(typeof(GhosttyNative.GhosttyTerminalResizeWindow), "GhosttyTerminalResizeWindow",
            new FieldMap("Tag", "tag"), new FieldMap("Rows", "value.grid.rows"),
            new FieldMap("Columns", "value.grid.columns")),
        new(typeof(GhosttyNative.GhosttyTerminalDesktopNotification), "GhosttyTerminalDesktopNotification",
            new FieldMap("Size", "size"), new FieldMap("Title", "title"),
            new FieldMap("Body", "body")),
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
        new("NO_VALUE", "GhosttyResult", "NO_VALUE"),

        new("OPT_USERDATA", "GhosttyTerminalOption", "USERDATA"),
        new("OPT_WRITE_PTY", "GhosttyTerminalOption", "WRITE_PTY"),
        new("OPT_BELL", "GhosttyTerminalOption", "BELL"),
        new("OPT_ENQUIRY", "GhosttyTerminalOption", "ENQUIRY"),
        new("OPT_TITLE_CHANGED", "GhosttyTerminalOption", "TITLE_CHANGED"),
        new("OPT_COLOR_FOREGROUND", "GhosttyTerminalOption", "COLOR_FOREGROUND"),
        new("OPT_COLOR_BACKGROUND", "GhosttyTerminalOption", "COLOR_BACKGROUND"),
        new("OPT_COLOR_CURSOR", "GhosttyTerminalOption", "COLOR_CURSOR"),
        new("OPT_COLOR_PALETTE", "GhosttyTerminalOption", "COLOR_PALETTE"),
        new("OPT_SELECTION", "GhosttyTerminalOption", "SELECTION"),
        new("OPT_PWD_CHANGED", "GhosttyTerminalOption", "PWD_CHANGED"),
        new("OPT_CLIPBOARD_WRITE", "GhosttyTerminalOption", "CLIPBOARD_WRITE"),
        new("OPT_CLIPBOARD_READ", "GhosttyTerminalOption", "CLIPBOARD_READ"),
        new("OPT_CLIPBOARD_WRITE_MAX_BYTES", "GhosttyTerminalOption", "CLIPBOARD_WRITE_MAX_BYTES"),
        new("OPT_SCROLLBACK_MAX_BYTES", "GhosttyTerminalOption", "SCROLLBACK_MAX_BYTES"),
        new("OPT_SCROLLBACK_MAX_LINES", "GhosttyTerminalOption", "SCROLLBACK_MAX_LINES"),
        new("OPT_DESKTOP_NOTIFICATION", "GhosttyTerminalOption", "DESKTOP_NOTIFICATION"),
        new("OPT_TITLE_REPORT", "GhosttyTerminalOption", "TITLE_REPORT"),
        new("OPT_XT_CHECKSUM_REPORT", "GhosttyTerminalOption", "XT_CHECKSUM_REPORT"),
        new("OPT_XT_CHECKSUM_EXTENSION", "GhosttyTerminalOption", "XT_CHECKSUM_EXTENSION"),
        new("OPT_PROGRAM_STATUS", "GhosttyTerminalOption", "PROGRAM_STATUS"),
        new("OPT_RESIZE_WINDOW", "GhosttyTerminalOption", "RESIZE_WINDOW"),
        new("RESIZE_WINDOW_GRID", "GhosttyTerminalResizeWindowTag", "GRID"),

        new("PROGRAM_STATUS_IDLE", "GhosttyProgramStatusState", "IDLE"),
        new("PROGRAM_STATUS_WORKING", "GhosttyProgramStatusState", "WORKING"),
        new("PROGRAM_STATUS_DONE", "GhosttyProgramStatusState", "DONE"),
        new("PROGRAM_STATUS_BLOCKED", "GhosttyProgramStatusState", "BLOCKED"),
        new("PROGRAM_STATUS_ERROR", "GhosttyProgramStatusState", "ERROR"),
        new("PROGRAM_STATUS_CLEAR", "GhosttyProgramStatusState", "CLEAR"),
        new("PROGRAM_STATUS_KIND_NONE", "GhosttyProgramStatusKind", "NONE"),
        new("PROGRAM_STATUS_KIND_PERMISSION", "GhosttyProgramStatusKind", "PERMISSION"),
        new("PROGRAM_STATUS_KIND_QUESTION", "GhosttyProgramStatusKind", "QUESTION"),
        new("PROGRAM_STATUS_KIND_AUTH", "GhosttyProgramStatusKind", "AUTH"),

        new("CLIPBOARD_LOCATION_STANDARD", "GhosttyClipboardLocation", "STANDARD"),
        new("CLIPBOARD_WRITE_SUCCESS", "GhosttyClipboardWriteResult", "SUCCESS"),
        new("CLIPBOARD_WRITE_DENIED", "GhosttyClipboardWriteResult", "DENIED"),
        new("CLIPBOARD_WRITE_UNSUPPORTED", "GhosttyClipboardWriteResult", "UNSUPPORTED"),
        new("CLIPBOARD_WRITE_INVALID_DATA", "GhosttyClipboardWriteResult", "INVALID_DATA"),
        new("CLIPBOARD_WRITE_IO_ERROR", "GhosttyClipboardWriteResult", "IO_ERROR"),
        new("CLIPBOARD_READ_SUCCESS", "GhosttyClipboardReadResult", "SUCCESS"),
        new("CLIPBOARD_READ_DENIED", "GhosttyClipboardReadResult", "DENIED"),
        new("CLIPBOARD_READ_UNSUPPORTED", "GhosttyClipboardReadResult", "UNSUPPORTED"),
        new("CLIPBOARD_READ_IO_ERROR", "GhosttyClipboardReadResult", "IO_ERROR"),

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
        new("SCROLL_ROW", "GhosttyTerminalScrollViewportTag", "ROW"),
        new("SCROLL_DELTA_PROMPT", "GhosttyTerminalScrollViewportTag", "DELTA_PROMPT"),

        new("POINT_VIEWPORT", "GhosttyPointTag", "VIEWPORT"),
        new("POINT_SCREEN", "GhosttyPointTag", "SCREEN"),

        new("SEARCH_STATUS_RUNNING", "GhosttySearchStatus", "RUNNING"),
        new("SEARCH_STATUS_FEED_REQUIRED", "GhosttySearchStatus", "FEED_REQUIRED"),
        new("SEARCH_STATUS_COMPLETE", "GhosttySearchStatus", "COMPLETE"),

        new("SEARCH_DATA_STATUS", "GhosttySearchData", "STATUS"),
        new("SEARCH_DATA_NEEDLE", "GhosttySearchData", "NEEDLE"),
        new("SEARCH_DATA_TOTAL_MATCHES", "GhosttySearchData", "TOTAL_MATCHES"),
        new("SEARCH_DATA_SELECTED_INDEX", "GhosttySearchData", "SELECTED_INDEX"),
        new("SEARCH_DATA_SELECTED_MATCH", "GhosttySearchData", "SELECTED_MATCH"),
        new("SEARCH_DATA_MATCHES", "GhosttySearchData", "MATCHES"),
        new("SEARCH_DATA_VIEWPORT_MATCHES", "GhosttySearchData", "VIEWPORT_MATCHES"),
        new("SEARCH_DATA_SELECT_SCROLL", "GhosttySearchData", "SELECT_SCROLL"),

        new("SEARCH_OPT_NEEDLE", "GhosttySearchOption", "NEEDLE"),
        new("SEARCH_OPT_SELECT_NEXT", "GhosttySearchOption", "SELECT_NEXT"),
        new("SEARCH_OPT_SELECT_PREV", "GhosttySearchOption", "SELECT_PREV"),
        new("SEARCH_OPT_SELECT_SCROLL", "GhosttySearchOption", "SELECT_SCROLL"),

        new("SEARCH_SCROLL_IF_NEEDED", "GhosttySearchScroll", "IF_NEEDED"),
        new("SEARCH_SCROLL_NONE", "GhosttySearchScroll", "NONE"),

        new("MOUSE_ACTION_PRESS", "GhosttyMouseAction", "PRESS"),
        new("MOUSE_ACTION_RELEASE", "GhosttyMouseAction", "RELEASE"),
        new("MOUSE_ACTION_MOTION", "GhosttyMouseAction", "MOTION"),
        new("MOUSE_BUTTON_LEFT", "GhosttyMouseButton", "LEFT"),
        new("MOUSE_BUTTON_RIGHT", "GhosttyMouseButton", "RIGHT"),
        new("MOUSE_BUTTON_MIDDLE", "GhosttyMouseButton", "MIDDLE"),
        new("MOUSE_BUTTON_FOUR", "GhosttyMouseButton", "FOUR"),
        new("MOUSE_BUTTON_FIVE", "GhosttyMouseButton", "FIVE"),

        new("KEY_ACTION_PRESS", "GhosttyKeyAction", "PRESS"),
        new("FORMAT_PLAIN", "GhosttyFormatterFormat", "PLAIN"),
        new("FORMAT_VT", "GhosttyFormatterFormat", "VT"),

        new("CURSOR_BAR", "GhosttyRenderStateCursorVisualStyle", "BAR"),
        new("CURSOR_BLOCK", "GhosttyRenderStateCursorVisualStyle", "BLOCK"),
        new("CURSOR_UNDERLINE", "GhosttyRenderStateCursorVisualStyle", "UNDERLINE"),
        new("CURSOR_BLOCK_HOLLOW", "GhosttyRenderStateCursorVisualStyle", "BLOCK_HOLLOW"),

        new("SNAPSHOT_DECODER_OPT_MAX_CONTINUATION_BYTES", "GhosttySnapshotDecoderOption", "MAX_CONTINUATION_BYTES"),
        new("SNAPSHOT_DECODER_OPT_RETAIN_CONTINUATION", "GhosttySnapshotDecoderOption", "RETAIN_CONTINUATION"),
        new("SNAPSHOT_DECODER_OPT_COMPRESS_HISTORY", "GhosttySnapshotDecoderOption", "COMPRESS_HISTORY"),
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

    // ---------------- mouse smoke ----------------

    /// <summary>
    /// Drives the mouse encoder through the same P/Invoke surface the app uses. The terminal
    /// is put into SGR mouse tracking mode, so a left press must encode to `ESC [ &lt; 0 ;`.
    /// </summary>
    private static unsafe void CheckMouseSmoke()
    {
        Section("mouse encoder smoke");

        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out IntPtr terminal, 80, 24) != GhosttyNative.SUCCESS)
        {
            Check("terminal created", false);
            return;
        }

        IntPtr encoder = IntPtr.Zero;
        IntPtr mouseEvent = IntPtr.Zero;
        try
        {
            byte[] modes = Encoding.ASCII.GetBytes("\u001b[?1000h\u001b[?1006h");
            fixed (byte* modePointer = modes)
                GhosttyNative.ghostty_terminal_vt_write(terminal, modePointer, (nuint)modes.Length);

            if (GhosttyNative.ghostty_mouse_encoder_new(IntPtr.Zero, out encoder) != GhosttyNative.SUCCESS ||
                GhosttyNative.ghostty_mouse_event_new(IntPtr.Zero, out mouseEvent) != GhosttyNative.SUCCESS)
            {
                Check("mouse encoder/event created", false);
                return;
            }

            Check("encoder follows terminal modes",
                GhosttyNative.ghostty_mouse_encoder_setopt_from_terminal(encoder, terminal)
                == GhosttyNative.SUCCESS);

            GhosttyNative.ghostty_mouse_event_set_action(mouseEvent, GhosttyNative.MOUSE_ACTION_PRESS);
            GhosttyNative.ghostty_mouse_event_set_button(mouseEvent, GhosttyNative.MOUSE_BUTTON_LEFT);
            GhosttyNative.ghostty_mouse_event_set_mods(mouseEvent, 0);
            // The encoder's default 1x1 cell grid makes the position cell coordinates.
            GhosttyNative.ghostty_mouse_event_set_position(mouseEvent,
                new GhosttyNative.GhosttyMousePosition { X = 1, Y = 0 });

            var buffer = new byte[128];
            fixed (byte* output = buffer)
            {
                int result = GhosttyNative.ghostty_mouse_encoder_encode(
                    encoder, mouseEvent, output, (nuint)buffer.Length, out nuint written);
                string text = result == GhosttyNative.SUCCESS && written > 0
                    ? Encoding.ASCII.GetString(buffer, 0, (int)written)
                    : string.Empty;
                Check("left press encodes SGR", text.StartsWith("\u001b[<0;", StringComparison.Ordinal),
                    text.Length == 0 ? "no output" : text.Replace("\u001b", "ESC"));
            }
        }
        finally
        {
            if (mouseEvent != IntPtr.Zero) GhosttyNative.ghostty_mouse_event_free(mouseEvent);
            if (encoder != IntPtr.Zero) GhosttyNative.ghostty_mouse_encoder_free(encoder);
            GhosttyNative.ghostty_terminal_free(terminal);
        }
    }

    /// <summary>
    /// Exercises the search API end to end: needle, scan, match count, selection, viewport
    /// matches and grid-ref to viewport conversion (the find-bar highlight path).
    /// </summary>
    private static unsafe void CheckSearchSmoke()
    {
        Section("search smoke");

        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out IntPtr terminal, 80, 24) != GhosttyNative.SUCCESS)
        {
            Check("terminal created", false);
            return;
        }

        IntPtr search = IntPtr.Zero;
        try
        {
            Check("search created",
                GhosttyNative.ghostty_search_new(IntPtr.Zero, out search, terminal) == GhosttyNative.SUCCESS);

            byte[] payload = Encoding.ASCII.GetBytes("hello world\r\nhello again\r\n");
            fixed (byte* pointer = payload)
                GhosttyNative.ghostty_terminal_vt_write(terminal, pointer, (nuint)payload.Length);

            byte[] needle = Encoding.ASCII.GetBytes("hello");
            fixed (byte* pointer = needle)
            {
                var value = new GhosttyNative.GhosttyString
                {
                    Ptr = (IntPtr)pointer,
                    Len = (nuint)needle.Length,
                };
                Check("needle set",
                    GhosttyNative.ghostty_search_set(search, GhosttyNative.SEARCH_OPT_NEEDLE, &value)
                    == GhosttyNative.SUCCESS);
            }

            Check("search runs", GhosttyNative.ghostty_search_run(search) == GhosttyNative.SUCCESS);

            ulong total = 0;
            Check("two matches",
                GhosttyNative.ghostty_search_get(search, GhosttyNative.SEARCH_DATA_TOTAL_MATCHES, &total)
                == GhosttyNative.SUCCESS && total == 2, $"total {total}");

            Check("select next",
                GhosttyNative.ghostty_search_set(search, GhosttyNative.SEARCH_OPT_SELECT_NEXT, null)
                == GhosttyNative.SUCCESS);

            ulong index = 0;
            Check("selected index",
                GhosttyNative.ghostty_search_get(search, GhosttyNative.SEARCH_DATA_SELECTED_INDEX, &index)
                == GhosttyNative.SUCCESS, $"index {index}");

            var selected = new GhosttyNative.GhosttySelection
            {
                Size = (nuint)sizeof(GhosttyNative.GhosttySelection),
            };
            Check("selected match",
                GhosttyNative.ghostty_search_get(search, GhosttyNative.SEARCH_DATA_SELECTED_MATCH, &selected)
                == GhosttyNative.SUCCESS);

            var query = new GhosttyNative.GhosttySelectionBuffer();
            int queryResult = GhosttyNative.ghostty_search_get(
                search, GhosttyNative.SEARCH_DATA_VIEWPORT_MATCHES, &query);
            Check("viewport capacity reported",
                queryResult == GhosttyNative.OUT_OF_SPACE && query.Len > 0,
                $"result {queryResult} len {query.Len}");

            if (query.Len > 0)
            {
                var items = new GhosttyNative.GhosttySelection[(int)query.Len];
                fixed (GhosttyNative.GhosttySelection* pointer = items)
                {
                    var buffer = new GhosttyNative.GhosttySelectionBuffer
                    {
                        Ptr = (IntPtr)pointer,
                        Cap = query.Len,
                        Len = 0,
                    };
                    Check("viewport matches read",
                        GhosttyNative.ghostty_search_get(
                            search, GhosttyNative.SEARCH_DATA_VIEWPORT_MATCHES, &buffer)
                        == GhosttyNative.SUCCESS && buffer.Len > 0,
                        $"len {buffer.Len}");

                    if (buffer.Len > 0)
                    {
                        var coordinate = new GhosttyNative.GhosttyPointCoordinate();
                        Check("match grid ref converts to viewport",
                            GhosttyNative.ghostty_terminal_point_from_grid_ref(
                                terminal, in items[0].Start, GhosttyNative.POINT_VIEWPORT, &coordinate)
                            == GhosttyNative.SUCCESS,
                            $"x {coordinate.X} y {coordinate.Y}");
                    }
                }
            }

            Check("needle cleared",
                GhosttyNative.ghostty_search_set(search, GhosttyNative.SEARCH_OPT_NEEDLE, null)
                == GhosttyNative.SUCCESS);
        }
        finally
        {
            if (search != IntPtr.Zero) GhosttyNative.ghostty_search_free(search);
            GhosttyNative.ghostty_terminal_free(terminal);
        }
    }

    /// <summary>
    /// Verifies the bell effect option: a BEL byte written into the terminal has to reach
    /// the registered callback, which is what "bell-features" hangs off.
    /// </summary>
    private static unsafe void CheckBellSmoke()
    {
        Section("bell smoke");

        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out IntPtr terminal, 80, 24) != GhosttyNative.SUCCESS)
        {
            Check("terminal created", false);
            return;
        }

        int fired = 0;
        GhosttyNative.NotifyFn callback = (_, _) => fired++;
        try
        {
            IntPtr functionPointer = Marshal.GetFunctionPointerForDelegate(callback);
            Check("bell callback registered",
                GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_BELL, (void*)functionPointer)
                == GhosttyNative.SUCCESS);

            byte[] bell = { 0x07 };
            fixed (byte* pointer = bell)
                GhosttyNative.ghostty_terminal_vt_write(terminal, pointer, (nuint)bell.Length);

            Check("BEL reaches the callback", fired == 1, $"fired {fired} time(s)");
            GC.KeepAlive(callback);
        }
        finally
        {
            GhosttyNative.ghostty_terminal_free(terminal);
        }
    }

    /// <summary>
    /// "title-report": CSI 21 t has to stay silent while the option is off, and once it is on
    /// the terminal answers with the title it holds, as OSC "l". The reply goes into the pty,
    /// so this also pins the exact bytes the shell receives.
    /// </summary>
    private static unsafe void CheckTitleReportSmoke()
    {
        Section("title-report smoke");

        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out IntPtr terminal, 80, 24) != GhosttyNative.SUCCESS)
        {
            Check("terminal created", false);
            return;
        }

        var reply = new StringBuilder();
        GhosttyNative.WritePtyFn capture = (_, _, data, len) =>
        {
            if (len == 0) return;
            var buffer = new byte[(int)len];
            Marshal.Copy(data, buffer, 0, buffer.Length);
            reply.Append(Encoding.UTF8.GetString(buffer));
        };

        try
        {
            IntPtr functionPointer = Marshal.GetFunctionPointerForDelegate(capture);
            Check("write callback registered",
                GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_WRITE_PTY, (void*)functionPointer)
                == GhosttyNative.SUCCESS);

            Feed(terminal, "\u001b]2;Gui Title\u001b\\");
            Feed(terminal, "\u001b[21t");
            Check("disabled report stays silent", reply.Length == 0,
                reply.Length == 0 ? "no output" : reply.ToString().Replace("\u001b", "ESC"));

            byte enabled = 1;
            Check("report option accepted",
                GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_TITLE_REPORT, &enabled)
                == GhosttyNative.SUCCESS);

            Feed(terminal, "\u001b[21t");
            const string expected = "\u001b]lGui Title\u001b\\";
            Check("enabled report answers with OSC l", reply.ToString() == expected,
                reply.Length == 0 ? "no output" : reply.ToString().Replace("\u001b", "ESC"));

            GC.KeepAlive(capture);
        }
        finally
        {
            GhosttyNative.ghostty_terminal_free(terminal);
        }

        static void Feed(IntPtr terminal, string text) => VtWrite(terminal, text);
    }

    /// <summary>Writes plain text into a terminal through the same entry point the app uses.</summary>
    private static unsafe void VtWrite(IntPtr terminal, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* pointer = bytes)
            GhosttyNative.ghostty_terminal_vt_write(terminal, pointer, (nuint)bytes.Length);
    }

    /// <summary>
    /// "vt-xt-checksum-report": DECRQCRA stays silent until the option is enabled, then
    /// answers with the DEC checksum. "vt-xt-checksum-extension" changes the calculation,
    /// here by turning off the final negation.
    /// </summary>
    private static unsafe void CheckXtChecksumSmoke()
    {
        Section("XT checksum smoke");

        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out IntPtr terminal, 80, 24) != GhosttyNative.SUCCESS)
        {
            Check("terminal created", false);
            return;
        }

        var reply = new StringBuilder();
        GhosttyNative.WritePtyFn capture = (_, _, data, len) =>
        {
            if (len == 0) return;
            var buffer = new byte[(int)len];
            Marshal.Copy(data, buffer, 0, buffer.Length);
            reply.Append(Encoding.UTF8.GetString(buffer));
        };

        try
        {
            IntPtr capturePointer = Marshal.GetFunctionPointerForDelegate(capture);
            Check("write callback registered",
                GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_WRITE_PTY, (void*)capturePointer)
                == GhosttyNative.SUCCESS);

            VtWrite(terminal, "A");
            VtWrite(terminal, "\u001b[1;1;1;1;1;1*y");
            Check("disabled report stays silent", reply.Length == 0,
                reply.Length == 0 ? "no output" : reply.ToString().Replace("\u001b", "ESC"));

            byte enabled = 1;
            Check("report option accepted",
                GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_XT_CHECKSUM_REPORT, &enabled)
                == GhosttyNative.SUCCESS);

            VtWrite(terminal, "\u001b[1;1;1;1;1;1*y");
            Check("enabled report answers with the DEC checksum", reply.ToString() == "\u001bP1!~FFBF\u001b\\",
                reply.Length == 0 ? "no output" : reply.ToString().Replace("\u001b", "ESC"));
            reply.Clear();

            byte extension = 1; // Bit 1: don't negate the sum.
            Check("extension option accepted",
                GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_XT_CHECKSUM_EXTENSION, &extension)
                == GhosttyNative.SUCCESS);

            VtWrite(terminal, "\u001b[1;1;1;1;1;1*y");
            Check("extension changes the calculation", reply.ToString() == "\u001bP1!~0041\u001b\\",
                reply.Length == 0 ? "no output" : reply.ToString().Replace("\u001b", "ESC"));

            GC.KeepAlive(capture);
        }
        finally
        {
            GhosttyNative.ghostty_terminal_free(terminal);
        }
    }

    /// <summary>
    /// "desktop-notifications": OSC 9 and OSC 777 reach the notification callback. OSC 9
    /// carries only a body; OSC 777 carries a title and a body.
    /// </summary>
    private static unsafe void CheckDesktopNotificationSmoke()
    {
        Section("desktop notification smoke");

        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out IntPtr terminal, 80, 24) != GhosttyNative.SUCCESS)
        {
            Check("terminal created", false);
            return;
        }

        int calls = 0;
        string title = string.Empty;
        string body = string.Empty;
        GhosttyNative.DesktopNotificationFn notify = (_, _, request) =>
        {
            var value = *(GhosttyNative.GhosttyTerminalDesktopNotification*)request;
            calls++;
            title = Text(value.Title);
            body = Text(value.Body);
        };

        try
        {
            IntPtr pointer = Marshal.GetFunctionPointerForDelegate(notify);
            Check("notification callback registered",
                GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_DESKTOP_NOTIFICATION, (void*)pointer)
                == GhosttyNative.SUCCESS);

            VtWrite(terminal, "\u001b]9;Hello world\u001b\\");
            Check("OSC 9 reaches the callback", calls == 1 && title.Length == 0 && body == "Hello world",
                $"calls={calls} title='{title}' body='{body}'");

            VtWrite(terminal, "\u001b]777;notify;Title;Body\u001b\\");
            Check("OSC 777 carries the title", calls == 2 && title == "Title" && body == "Body",
                $"calls={calls} title='{title}' body='{body}'");

            GC.KeepAlive(notify);
        }
        finally
        {
            GhosttyNative.ghostty_terminal_free(terminal);
        }

        static string Text(GhosttyNative.GhosttyString value) =>
            value.Ptr == IntPtr.Zero || value.Len == 0
                ? string.Empty
                : Marshal.PtrToStringUTF8(value.Ptr, (int)value.Len) ?? string.Empty;
    }

    /// <summary>
    /// "vt-window-resize-allowed": CSI 8 ; rows ; columns t reaches the resize callback with
    /// the grid size packed in the union arm the app reads.
    /// </summary>
    private static unsafe void CheckResizeWindowSmoke()
    {
        Section("resize window smoke");

        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out IntPtr terminal, 80, 24) != GhosttyNative.SUCCESS)
        {
            Check("terminal created", false);
            return;
        }

        int rows = -1, columns = -1, calls = 0;
        GhosttyNative.ResizeWindowFn resize = (_, _, request) =>
        {
            var value = *(GhosttyNative.GhosttyTerminalResizeWindow*)request;
            calls++;
            rows = value.Rows;
            columns = value.Columns;
        };

        try
        {
            IntPtr pointer = Marshal.GetFunctionPointerForDelegate(resize);
            Check("resize callback registered",
                GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_RESIZE_WINDOW, (void*)pointer)
                == GhosttyNative.SUCCESS);

            VtWrite(terminal, "\u001b[8;30;100t");
            Check("CSI 8 t reaches the callback", calls == 1 && rows == 30 && columns == 100,
                $"calls={calls} rows={rows} columns={columns}");

            GC.KeepAlive(resize);
        }
        finally
        {
            GhosttyNative.ghostty_terminal_free(terminal);
        }
    }

    /// <summary>
    /// "enquiry-response": ENQ (0x05) stays silent while the response is empty and writes the
    /// configured bytes back otherwise. The answer arrives through the write-PTY callback,
    /// which is where the app's terminals receive it.
    /// </summary>
    private static unsafe void CheckEnquirySmoke()
    {
        Section("enquiry smoke");

        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out IntPtr terminal, 80, 24) != GhosttyNative.SUCCESS)
        {
            Check("terminal created", false);
            return;
        }

        var reply = new StringBuilder();
        GhosttyNative.WritePtyFn capture = (_, _, data, len) =>
        {
            if (len == 0) return;
            var buffer = new byte[(int)len];
            Marshal.Copy(data, buffer, 0, buffer.Length);
            reply.Append(Encoding.UTF8.GetString(buffer));
        };

        byte[] response = Array.Empty<byte>();
        GCHandle pin = default;
        GhosttyNative.EnquiryFn enquiry = (_, _) => pin.IsAllocated
            ? new GhosttyNative.GhosttyString
            {
                Ptr = pin.AddrOfPinnedObject(),
                Len = (nuint)response.Length,
            }
            : default;

        try
        {
            IntPtr capturePointer = Marshal.GetFunctionPointerForDelegate(capture);
            IntPtr enquiryPointer = Marshal.GetFunctionPointerForDelegate(enquiry);
            Check("callbacks registered",
                GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_WRITE_PTY, (void*)capturePointer)
                == GhosttyNative.SUCCESS &&
                GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_ENQUIRY, (void*)enquiryPointer)
                == GhosttyNative.SUCCESS);

            VtWrite(terminal, "\u0005");
            Check("empty response stays silent", reply.Length == 0,
                reply.Length == 0 ? "no output" : reply.ToString());

            response = Encoding.UTF8.GetBytes("gui");
            pin = GCHandle.Alloc(response, GCHandleType.Pinned);
            VtWrite(terminal, "\u0005");
            Check("configured response reaches the pty", reply.ToString() == "gui",
                reply.Length == 0 ? "no output" : reply.ToString());

            GC.KeepAlive(capture);
            GC.KeepAlive(enquiry);
        }
        finally
        {
            if (pin.IsAllocated) pin.Free();
            GhosttyNative.ghostty_terminal_free(terminal);
        }
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

    /// <summary>
    /// Drives the snapshot encoder/decoder through the same P/Invoke surface the app uses for
    /// "window-save-state": history written before the encode must come back after the decode.
    /// </summary>
    private static unsafe void CheckSnapshotSmoke()
    {
        Section("snapshot smoke");

        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out IntPtr terminal, 40, 8) != GhosttyNative.SUCCESS)
        {
            Check("terminal created", false);
            return;
        }

        IntPtr buffer = IntPtr.Zero;
        IntPtr decoder = IntPtr.Zero;
        IntPtr restored = IntPtr.Zero;
        nuint length = 0;
        try
        {
            var text = new StringBuilder();
            for (int i = 0; i < 50; i++) text.Append("line ").Append(i).Append("\r\n");
            byte[] payload = Encoding.ASCII.GetBytes(text.ToString());
            fixed (byte* pointer = payload)
                GhosttyNative.ghostty_terminal_vt_write(terminal, pointer, (nuint)payload.Length);

            GhosttyNative.GhosttyTerminalScrollbar before;
            if (GhosttyNative.ghostty_terminal_get(terminal, GhosttyNative.TDATA_SCROLLBAR, &before) != GhosttyNative.SUCCESS)
                throw new InvalidOperationException("ghostty_terminal_get(SCROLLBAR) failed");

            int encode = GhosttyNative.ghostty_snapshot_encode_alloc(
                terminal, IntPtr.Zero, out buffer, out length);
            Check("encode succeeds", encode == GhosttyNative.SUCCESS && length > 0,
                $"result={encode} bytes={length}");

            if (encode == GhosttyNative.SUCCESS && buffer != IntPtr.Zero && length > 0)
            {
                if (GhosttyNative.ghostty_snapshot_decoder_new_buf(
                        IntPtr.Zero, out decoder, (byte*)buffer, length) != GhosttyNative.SUCCESS)
                    throw new InvalidOperationException("snapshot decoder could not be created");

                bool compress = true;
                Check("decoder accepts compress_history",
                    GhosttyNative.ghostty_snapshot_decoder_set(
                        decoder, GhosttyNative.SNAPSHOT_DECODER_OPT_COMPRESS_HISTORY, &compress) ==
                    GhosttyNative.SUCCESS);

                int decode = GhosttyNative.ghostty_snapshot_decoder_decode(decoder, out restored);
                Check("decode succeeds", decode == GhosttyNative.SUCCESS && restored != IntPtr.Zero,
                    $"result={decode}");

                if (restored != IntPtr.Zero)
                {
                    ushort cols = 0, rows = 0;
                    GhosttyNative.ghostty_terminal_get(restored, GhosttyNative.TDATA_COLS, &cols);
                    GhosttyNative.ghostty_terminal_get(restored, GhosttyNative.TDATA_ROWS, &rows);
                    Check("restored grid matches", cols == 40 && rows == 8, $"{cols}x{rows}");

                    GhosttyNative.GhosttyTerminalScrollbar after;
                    if (GhosttyNative.ghostty_terminal_get(restored, GhosttyNative.TDATA_SCROLLBAR, &after) == GhosttyNative.SUCCESS)
                        Check("scrollback comes back", after.Total == before.Total,
                            $"{after.Total} of {before.Total}");
                }
            }
        }
        finally
        {
            if (restored != IntPtr.Zero) GhosttyNative.ghostty_terminal_free(restored);
            if (decoder != IntPtr.Zero) GhosttyNative.ghostty_snapshot_decoder_free(decoder);
            if (buffer != IntPtr.Zero) GhosttyNative.ghostty_free(IntPtr.Zero, buffer, length);
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
