using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Gui;

/// <summary>Severity of a diagnostic record.</summary>
internal enum DiagLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,

    /// <summary>Logging is switched off entirely.</summary>
    Off = 4,
}

/// <summary>
/// Startup and runtime diagnostics: a failed activation reports a bare process exit code and
/// an exception escaping a XAML callback turns into a crash, so record the detail while it
/// is still known. Unpackaged apps have no package identity, so the log goes to %TEMP%.
///
/// The log is a plain text file, one record per line:
/// "2026-11-05 21:40:13.123 INFO  [scope] detail". Verbosity comes from the GUI_LOG
/// environment variable ("debug", "info", "warn", "error", "off"; default "info"), so the
/// chatty records are compiled in but only written when asked for. The file is rotated once
/// it grows past a couple of megabytes, keeping one previous generation.
/// </summary>
internal static class Diag
{
    private const string FileName = "gui-startup.log";
    private const string PreviousFileName = "gui-startup.log.1";
    private const string LevelVariable = "GUI_LOG";

    /// <summary>Rotate once the log passes this size.</summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static bool _headerWritten;
    private static bool _failed;
    private static DiagLevel _level = ParseLevel(Environment.GetEnvironmentVariable(LevelVariable));

    /// <summary>%TEMP%\gui-startup.log.</summary>
    public static string LogPath
    {
        get
        {
            string dir = Path.GetTempPath();
            if (string.IsNullOrEmpty(dir)) return FileName;
            return Path.Combine(dir, FileName);
        }
    }

    public static DiagLevel Level => _level;

    /// <summary>Whether records of this severity are written.</summary>
    public static bool IsEnabled(DiagLevel level) => _level != DiagLevel.Off && level >= _level;

    /// <summary>
    /// Maps the GUI_LOG value to a level. Unknown or missing values keep the default so a
    /// typo cannot silence the diagnostics.
    /// </summary>
    internal static DiagLevel ParseLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DiagLevel.Info;

        return value.Trim().ToLowerInvariant() switch
        {
            "debug" or "trace" or "verbose" => DiagLevel.Debug,
            "info" => DiagLevel.Info,
            "warn" or "warning" => DiagLevel.Warn,
            "error" or "err" => DiagLevel.Error,
            "off" or "none" or "silent" => DiagLevel.Off,
            _ => DiagLevel.Info,
        };
    }

    public static void Log(string where, string detail) => Write(DiagLevel.Info, where, detail);

    public static void Debug(string where, string detail) => Write(DiagLevel.Debug, where, detail);

    public static void Warn(string where, string detail) => Write(DiagLevel.Warn, where, detail);

    public static void Error(string where, string detail) => Write(DiagLevel.Error, where, detail);

    public static void LogException(string where, Exception error)
    {
        Write(DiagLevel.Error, where, $"0x{error.HResult:X8}: {error.GetType().Name}: {error.Message}");
        if (error.StackTrace is { Length: > 0 } stack)
            Write(DiagLevel.Error, where + ".stack", stack);
    }

    /// <summary>
    /// Runs a callback that XAML or a timer invokes with no one above it to catch: an
    /// exception escaping one of these is turned into an application fail-fast
    /// (0xC000027B) with no managed stack, so log it and keep the app alive instead.
    /// </summary>
    public static void Guard(string where, Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            LogException(where, error);
        }
    }

    public static void InstallHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error)
                LogException("AppDomain.UnhandledException", error);
            else
                Write(DiagLevel.Error, "AppDomain.UnhandledException", "unknown exception type");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogException("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>The whole log, for the "copy diagnostics" action.</summary>
    public static string ReadAll()
    {
        try
        {
            lock (Gate)
            {
                _writer?.Flush();
                string path = LogPath;
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---------------- writing ----------------

    private static void Write(DiagLevel level, string where, string detail)
    {
        if (!IsEnabled(level)) return;

        string line = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss.fff} {1,-5} [{2}] {3}",
            DateTime.Now, level.ToString().ToUpperInvariant(), where, detail);

        // The debugger output is always available, even when the file cannot be written.
        System.Diagnostics.Debug.WriteLine(line);

        lock (Gate)
        {
            if (_failed) return;

            try
            {
                StreamWriter writer = _writer ?? Open();
                if (!_headerWritten)
                {
                    _headerWritten = true;
                    writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "--- session pid={0} started={1:yyyy-MM-ddTHH:mm:ss} level={2} ---",
                        Environment.ProcessId, DateTime.Now, _level));
                }
                writer.WriteLine(line);
            }
            catch
            {
                // Diagnostics must never throw, and a broken log must not retry forever.
                _failed = true;
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private static StreamWriter Open()
    {
        string path = LogPath;
        Rotate(path);

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _writer = writer;
        return writer;
    }

    /// <summary>Keeps the previous generation and starts a fresh file.</summary>
    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;

            string previous = Path.Combine(
                Path.GetDirectoryName(path) ?? string.Empty, PreviousFileName);
            File.Move(path, previous, overwrite: true);
        }
        catch
        {
            // Rotation is best effort; keep logging to the current file.
        }
    }
}
