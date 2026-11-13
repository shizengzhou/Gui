using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gui.Term;

namespace Gui.PerfTests;

/// <summary>
/// Release performance regression harness for the libghostty-vt pipeline the app drives:
/// terminal parsing, the render-state walk that runs once per painted frame, and scrollback
/// trimming. It is headless on purpose: the shipped P/Invoke declarations are compiled in and
/// exercised through the same calls TerminalSession and TerminalRenderer make, so a regression
/// in the hot path is measured without a WinUI window, a compositor or a GPU in the way.
///
/// Run:      dotnet run --project tests/Gui.PerfTests -c Release
/// New base: dotnet run --project tests/Gui.PerfTests -c Release -- --save
/// Exit:     0 = within tolerance (or a baseline was written), 1 = regression, 2 = could not run.
/// </summary>
internal static class Program
{
    private const string DllName = "ghostty-vt.dll";

    /// <summary>How much worse than the baseline a metric may get before it fails.</summary>
    private const double Tolerance = 0.25;

    /// <summary>The ConPty reader reads 64 KiB at a time, so that is the chunk the app parses.</summary>
    private const int ChunkBytes = 64 * 1024;

    private const int Repetitions = 5;

    private static string _dllPath = string.Empty;

    private sealed record Result(string Name, string Unit, bool HigherIsBetter, double Value, double Spread);

    private static int Main(string[] args)
    {
        try
        {
            string root = FindRepositoryRoot();
            _dllPath = FindDll();
            NativeLibrary.SetDllImportResolver(typeof(GhosttyNative).Assembly, ImportResolver);

            bool save = args.Contains("--save", StringComparer.OrdinalIgnoreCase);
            bool compare = !args.Contains("--no-compare", StringComparer.OrdinalIgnoreCase);
            string? only = Value(args, "--only");
            string baselinePath = Path.Combine(root, "tests", "Gui.PerfTests", "baseline.json");

            PrintEnvironment();
            List<Result> results = RunAll(only);

            if (save)
            {
                Save(baselinePath, results);
                Console.WriteLine();
                Console.WriteLine("baseline written: " + Path.GetRelativePath(root, baselinePath));
                return 0;
            }

            if (!compare)
            {
                PrintResults(results, null);
                return 0;
            }

            if (!File.Exists(baselinePath))
            {
                Console.WriteLine();
                Console.WriteLine("no baseline at " + Path.GetRelativePath(root, baselinePath) +
                    " - run once with --save on this machine first");
                PrintResults(results, null);
                return 0;
            }

            Dictionary<string, double> baseline = Load(baselinePath);
            bool failed = PrintResults(results, baseline);

            Console.WriteLine();
            Console.WriteLine(failed
                ? "FAIL: at least one metric regressed by more than " + Percent(Tolerance)
                : "PASS: every metric is within " + Percent(Tolerance) + " of the baseline");
            return failed ? 1 : 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("perf harness failed: " + error);
            return 2;
        }
    }

    // ---------------- scenarios ----------------

    private static List<Result> RunAll(string? only)
    {
        bool Wanted(string name) => only is null || name.StartsWith(only, StringComparison.Ordinal);

        var results = new List<Result>();
        if (Wanted("parse/ascii")) results.Add(MeasureParse("parse/ascii", BuildPlain(8 * 1024 * 1024)));
        if (Wanted("parse/sgr")) results.Add(MeasureParse("parse/sgr-24bit", BuildSgr(8 * 1024 * 1024)));
        if (Wanted("parse/unicode")) results.Add(MeasureParse("parse/unicode", BuildUnicode(4 * 1024 * 1024)));
        if (Wanted("frame/")) results.Add(MeasureFrame());
        if (Wanted("scrollback/")) results.AddRange(MeasureScrollbackTrim());
        return results;
    }

    /// <summary>Value of "--name value", or null when the switch is absent.</summary>
    private static string? Value(string[] args, string name)
    {
        int index = Array.FindIndex(args, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// Bytes per second the parser sustains. The corpus is fed in ConPty-sized chunks through a
    /// fresh terminal whose scrollback is large enough that the run does not trim.
    /// </summary>
    private static unsafe Result MeasureParse(string name, byte[] corpus)
    {
        var samples = new List<double>(Repetitions);

        for (int repetition = 0; repetition < Repetitions; repetition++)
        {
            IntPtr terminal = NewTerminal(200, 50, scrollbackBytes: 256UL * 1024 * 1024);
            try
            {
                fixed (byte* pointer = corpus)
                {
                    // Warm up the pages and the parser's first-sight allocations off the clock.
                    GhosttyNative.ghostty_terminal_vt_write(terminal, pointer,
                        (nuint)Math.Min(corpus.Length, ChunkBytes * 8));

                    var watch = Stopwatch.StartNew();
                    for (int offset = 0; offset < corpus.Length; offset += ChunkBytes)
                    {
                        int count = Math.Min(ChunkBytes, corpus.Length - offset);
                        GhosttyNative.ghostty_terminal_vt_write(terminal, pointer + offset, (nuint)count);
                    }
                    watch.Stop();

                    samples.Add(corpus.Length / (1024.0 * 1024.0) / watch.Elapsed.TotalSeconds);
                }
            }
            finally
            {
                GhosttyNative.ghostty_terminal_free(terminal);
            }
        }

        return new Result(name, "MB/s", HigherIsBetter: true, Best(samples, true), Spread(samples));
    }

    /// <summary>
    /// Cost of one painted frame's read path: apply one line of output, update the render state,
    /// walk every row and cell and read the style, colors and text the renderer needs. This is
    /// the CPU work that has to fit inside a frame; the Win2D drawing itself is not included.
    /// </summary>
    private static unsafe Result MeasureFrame()
    {
        const ushort cols = 120;
        const ushort rows = 40;
        const int frames = 2000;

        byte[] line = Encoding.ASCII.GetBytes(
            "\u001b[38;2;120;200;255m  build  \u001b[0m 12:00:00  INFO  worker[3]  " +
            "wrote 128 bytes to C:\\dev\\project\\out\\artifact.bin                 \r\n");

        var samples = new List<double>(Repetitions);

        for (int repetition = 0; repetition < Repetitions; repetition++)
        {
            IntPtr terminal = NewTerminal(cols, rows, scrollbackBytes: 64UL * 1024 * 1024);
            IntPtr state = IntPtr.Zero;
            IntPtr rowIterator = IntPtr.Zero;
            IntPtr cellIterator = IntPtr.Zero;
            try
            {
                if (GhosttyNative.ghostty_render_state_new(IntPtr.Zero, out state) != GhosttyNative.SUCCESS ||
                    GhosttyNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out rowIterator) != GhosttyNative.SUCCESS ||
                    GhosttyNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out cellIterator) != GhosttyNative.SUCCESS)
                {
                    throw new InvalidOperationException("could not create the render state");
                }

                fixed (byte* pointer = line)
                {
                    // Fill the viewport first, then let the frame loop scroll it.
                    for (int i = 0; i < rows; i++)
                        GhosttyNative.ghostty_terminal_vt_write(terminal, pointer, (nuint)line.Length);

                    var watch = Stopwatch.StartNew();
                    for (int frame = 0; frame < frames; frame++)
                    {
                        GhosttyNative.ghostty_terminal_vt_write(terminal, pointer, (nuint)line.Length);
                        GhosttyNative.ghostty_render_state_update(state, terminal);
                        GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_ROW_ITERATOR, &rowIterator);
                        WalkFrame(rowIterator, cellIterator);
                    }
                    watch.Stop();

                    samples.Add(watch.Elapsed.TotalMilliseconds / frames);
                }
            }
            finally
            {
                if (cellIterator != IntPtr.Zero) GhosttyNative.ghostty_render_state_row_cells_free(cellIterator);
                if (rowIterator != IntPtr.Zero) GhosttyNative.ghostty_render_state_row_iterator_free(rowIterator);
                if (state != IntPtr.Zero) GhosttyNative.ghostty_render_state_free(state);
                GhosttyNative.ghostty_terminal_free(terminal);
            }
        }

        return new Result("frame/read-path", "ms/frame", HigherIsBetter: false, Best(samples, false), Spread(samples));
    }

    private static unsafe void WalkFrame(IntPtr rowIterator, IntPtr cellIterator)
    {
        int cells = 0;
        while (GhosttyNative.ghostty_render_state_row_iterator_next(rowIterator))
        {
            if (GhosttyNative.ghostty_render_state_row_get(rowIterator, GhosttyNative.ROW_DATA_CELLS, &cellIterator) != GhosttyNative.SUCCESS)
                continue;

            while (GhosttyNative.ghostty_render_state_row_cells_next(cellIterator))
            {
                var style = new GhosttyNative.GhosttyStyle();
                GhosttyNative.ghostty_render_state_row_cells_get(cellIterator, GhosttyNative.CELL_STYLE, &style);

                Rgb background;
                Rgb foreground;
                GhosttyNative.ghostty_render_state_row_cells_get(cellIterator, GhosttyNative.CELL_BG_COLOR, &background);
                GhosttyNative.ghostty_render_state_row_cells_get(cellIterator, GhosttyNative.CELL_FG_COLOR, &foreground);

                cells += CellText.Read(cellIterator).Length;
            }
        }

        // Keeps the walk observable so a future JIT cannot elide the reads.
        if (cells < 0) Console.Error.WriteLine("unreachable");
    }

    /// <summary>
    /// Scrollback throughput and retention: 64 MiB of output through a 16 MiB cap forces the
    /// trim path for most of the run, then the private footprint shows what is retained.
    /// </summary>
    private static unsafe List<Result> MeasureScrollbackTrim()
    {
        byte[] corpus = BuildPlain(64 * 1024 * 1024);
        var throughput = new List<double>(Repetitions);

        for (int repetition = 0; repetition < Repetitions; repetition++)
        {
            IntPtr terminal = NewTerminal(200, 50, scrollbackBytes: 16UL * 1024 * 1024);
            try
            {
                fixed (byte* pointer = corpus)
                {
                    var watch = Stopwatch.StartNew();
                    for (int offset = 0; offset < corpus.Length; offset += ChunkBytes)
                    {
                        int count = Math.Min(ChunkBytes, corpus.Length - offset);
                        GhosttyNative.ghostty_terminal_vt_write(terminal, pointer + offset, (nuint)count);
                    }
                    watch.Stop();
                    throughput.Add(corpus.Length / (1024.0 * 1024.0) / watch.Elapsed.TotalSeconds);
                }
            }
            finally
            {
                GhosttyNative.ghostty_terminal_free(terminal);
            }
        }

        // Drop the corpus before reading the footprint: the metric is what the runtime and the
        // terminal retain, not what the harness needed to feed them.
        corpus = Array.Empty<byte>();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        double retained = Process.GetCurrentProcess().PrivateMemorySize64 / (1024.0 * 1024.0);

        return new List<Result>
        {
            new("scrollback/trim", "MB/s", HigherIsBetter: true, Best(throughput, true), Spread(throughput)),
            new("scrollback/private-mb", "MB", HigherIsBetter: false, retained, 0),
        };
    }

    // ---------------- corpora ----------------

    private static byte[] BuildPlain(int targetBytes)
    {
        var lines = new byte[8][];
        for (int index = 0; index < lines.Length; index++)
        {
            string line = index.ToString(CultureInfo.InvariantCulture) +
                "  2026-11-13T12:00:00.000Z  INFO  worker[" + (index % 16) +
                "]  completed request in " + (index % 997) + " ms  path=/api/v1/items/" + index;
            if (line.Length > 118) line = line.Substring(0, 118);
            lines[index] = Encoding.UTF8.GetBytes(line + "\r\n");
        }

        return Repeat(lines, targetBytes);
    }

    private static byte[] BuildSgr(int targetBytes)
    {
        var lines = new byte[8][];
        for (int index = 0; index < lines.Length; index++)
        {
            string line = "\u001b[38;2;" + (index * 31 % 256) + ";" + (index * 53 % 256) + ";" +
                (index * 97 % 256) + "mcolored segment " + index +
                "\u001b[1m bold\u001b[0m plain tail text for the line";
            lines[index] = Encoding.UTF8.GetBytes(line + "\r\n");
        }

        return Repeat(lines, targetBytes);
    }

    private static byte[] BuildUnicode(int targetBytes)
    {
        // CJK, Japanese kana, Hangul, an emoji sequence and combining marks: the paths where the
        // parser has to join UTF-8 across chunk boundaries and measure grapheme width.
        string[] samples =
        {
            "\u65e5\u5fd7\u8f93\u51fa\uff1a\u4e2d\u6587\u6d4b\u8bd5 0123456789",
            "\u30c6\u30ad\u30b9\u30c8\u3092\u66f4\u65b0\u3057\u307e\u3057\u305f",
            "\ud55c\uad6d\uc5b4 \ud14c\uc2a4\ud2b8 \uc644\ub8cc",
            "\ud83d\ude80\ud83c\udf89 deployed \u2705 to production",
            "e\u0301 a\u0308 n\u0303 combining marks",
        };

        var lines = new byte[samples.Length][];
        for (int index = 0; index < samples.Length; index++)
            lines[index] = Encoding.UTF8.GetBytes(samples[index] + "\r\n");

        return Repeat(lines, targetBytes);
    }

    /// <summary>Fills a buffer with whole lines, so a corpus never ends mid-sequence.</summary>
    private static byte[] Repeat(byte[][] lines, int targetBytes)
    {
        int cycle = 0;
        foreach (byte[] line in lines) cycle += line.Length;

        var buffer = new byte[(targetBytes + cycle - 1) / cycle * cycle];
        int offset = 0;
        for (int index = 0; offset < buffer.Length; index++)
        {
            byte[] line = lines[index % lines.Length];
            Buffer.BlockCopy(line, 0, buffer, offset, line.Length);
            offset += line.Length;
        }

        return buffer;
    }

    // ---------------- native plumbing ----------------

    private static unsafe IntPtr NewTerminal(ushort cols, ushort rows, ulong scrollbackBytes)
    {
        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out IntPtr terminal, cols, rows) != GhosttyNative.SUCCESS)
            throw new InvalidOperationException("ghostty_terminal_new failed");

        nuint limit = (nuint)scrollbackBytes;
        if (GhosttyNative.ghostty_terminal_set(terminal, GhosttyNative.OPT_SCROLLBACK_MAX_BYTES, &limit) != GhosttyNative.SUCCESS)
        {
            GhosttyNative.ghostty_terminal_free(terminal);
            throw new InvalidOperationException("ghostty_terminal_set(SCROLLBACK_MAX_BYTES) failed");
        }

        return terminal;
    }

    private static IntPtr ImportResolver(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name is "ghostty-vt" or DllName ? NativeLibrary.Load(_dllPath) : IntPtr.Zero;

    private static string FindDll()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("GUI_GHOSTTY_DLL");
        if (!string.IsNullOrEmpty(fromEnvironment))
        {
            if (!File.Exists(fromEnvironment))
                throw new FileNotFoundException("GUI_GHOSTTY_DLL does not exist", fromEnvironment);
            return fromEnvironment;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string sourceTree = Path.Combine(dir.FullName, "Gui", "Gui", "native", "x64", DllName);
            if (File.Exists(sourceTree)) return sourceTree;

            string besideExe = Path.Combine(dir.FullName, DllName);
            if (File.Exists(besideExe)) return besideExe;
        }

        throw new FileNotFoundException("Could not locate " + DllName + ". Set GUI_GHOSTTY_DLL to the full path.");
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Gui.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root (Gui.slnx).");
    }

    // ---------------- reporting ----------------

    private static void PrintEnvironment()
    {
        Console.WriteLine("== environment ==");
        Console.WriteLine("dll          " + _dllPath);
        Console.WriteLine("dll sha256   " + Hash(_dllPath));
        Console.WriteLine("runtime      " + RuntimeInformation.FrameworkDescription);
        Console.WriteLine("cpu          " + Environment.ProcessorCount + " logical");
        Console.WriteLine("captured     " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        Console.WriteLine();
        Console.WriteLine("== results ==");
    }

    private static bool PrintResults(List<Result> results, Dictionary<string, double>? baseline)
    {
        bool failed = false;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-24} {1,10} {2,-9} {3,10} {4,9} {5,7}  {6}",
            "metric", "value", "unit", "baseline", "delta", "spread", "status"));

        foreach (Result result in results)
        {
            string delta = string.Empty;
            string status = "new";

            if (baseline is not null)
            {
                if (baseline.TryGetValue(result.Name, out double expected) && expected > 0)
                {
                    double change = (result.Value - expected) / expected;
                    bool regressed = result.HigherIsBetter
                        ? change < -Tolerance
                        : change > Tolerance;

                    delta = (change >= 0 ? "+" : string.Empty) + Percent(change);
                    status = regressed ? "REGRESSED" : "ok";
                    failed |= regressed;
                }
                else
                {
                    status = "no baseline";
                }
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-24} {1,10:0.##} {2,-9} {3,10} {4,9} {5,7}  {6}",
                result.Name, result.Value, result.Unit,
                baseline is not null && baseline.TryGetValue(result.Name, out double value)
                    ? value.ToString("0.##", CultureInfo.InvariantCulture)
                    : "-",
                delta, result.Spread > 0 ? Percent(result.Spread) : "-", status));
        }

        return failed;
    }

    private static void Save(string path, List<Result> results)
    {
        var payload = new Dictionary<string, object>
        {
            ["captured"] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["processorCount"] = Environment.ProcessorCount,
            ["dllSha256"] = Hash(_dllPath),
            ["tolerance"] = Tolerance,
            ["spread"] = results.ToDictionary(result => result.Name, result => result.Spread),
            ["metrics"] = results.ToDictionary(
                result => result.Name,
                result => new Dictionary<string, object> { ["value"] = result.Value, ["unit"] = result.Unit }),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + "\n",
            new UTF8Encoding(false));
    }

    private static Dictionary<string, double> Load(string path)
    {
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (JsonProperty property in document.RootElement.GetProperty("metrics").EnumerateObject())
            metrics[property.Name] = property.Value.GetProperty("value").GetDouble();
        return metrics;
    }

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant().Substring(0, 16);
    }

    /// <summary>
    /// The best sample, not the median: a preempted run must not look like a regression, and a
    /// real regression slows every sample down.
    /// </summary>
    private static double Best(List<double> samples, bool higherIsBetter) =>
        higherIsBetter ? samples.Max() : samples.Min();

    private static double Spread(List<double> samples)
    {
        double median = Median(samples);
        return median <= 0 ? 0 : (samples[^1] - samples[0]) / median;
    }

    private static double Median(List<double> samples)
    {
        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static string Percent(double value) =>
        (value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
