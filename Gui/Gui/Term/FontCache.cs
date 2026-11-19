using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.Text;

namespace Gui.Term;

/// <summary>
/// The process-wide system font collection, shared by the terminal renderer and the settings
/// font picker. Mirrors Windows Terminal's FontCache: load DirectWrite's font set once, let
/// every consumer enumerate that same set, and drop it on a config reload so newly installed
/// fonts are seen. Win2D's GetSystemFontSet() plus a full face walk costs ~300 ms, so doing it
/// once per process instead of twice is the whole point.
/// </summary>
internal static class FontCache
{
    private const uint FR_PRIVATE = 0x10;

    private static readonly string[] BundledFontFiles =
    {
        "JetBrainsMonoNerdFont-Regular.ttf",
        "JetBrainsMonoNerdFont-Bold.ttf",
        "JetBrainsMonoNerdFont-Italic.ttf",
        "JetBrainsMonoNerdFont-BoldItalic.ttf",
    };

    private static readonly object Gate = new();
    private static bool _fontsRegistered;
    private static CanvasFontSet? _set;
    private static List<string>? _monospaceFamilies;
    private static Task<IReadOnlyList<string>>? _pendingFamilies;

    /// <summary>
    /// Registers the bundled Nerd Font faces with the process. Win2D's system font set does not
    /// include font files it was not told about, so this must run before the first Get(). It is
    /// idempotent; both the renderer constructor and Get() call it to guarantee the ordering.
    /// </summary>
    internal static void RegisterBundledFonts()
    {
        lock (Gate)
        {
            if (_fontsRegistered) return;
            _fontsRegistered = true;

            string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
            foreach (string name in BundledFontFiles)
            {
                string file = Path.Combine(directory, name);
                if (File.Exists(file)) AddFontResourceEx(file, FR_PRIVATE, IntPtr.Zero);
            }
        }
    }

    /// <summary>The shared system font set, loaded on first use.</summary>
    internal static CanvasFontSet Get()
    {
        lock (Gate)
        {
            RegisterBundledFonts();
            return _set ??= CanvasFontSet.GetSystemFontSet();
        }
    }

    /// <summary>
    /// Every family DirectWrite marks as monospaced, sorted by name, plus the bundled default.
    /// Derived once from the shared set so opening Settings never walks the faces itself.
    /// </summary>
    internal static IReadOnlyList<string> MonospaceFamilies(string bundledFamily)
    {
        lock (Gate)
        {
            if (_monospaceFamilies is null)
            {
                var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (CanvasFontFace face in Get().Fonts)
                    {
                        if (!face.IsMonospaced) continue;
                        foreach (KeyValuePair<string, string> pair in face.FamilyNames)
                        {
                            names.Add(pair.Value);
                            break;
                        }
                    }
                }
                catch (Exception error)
                {
                    Diag.LogException("Workspace.MonospaceFontFamilies", error);
                }
                names.Add(bundledFamily);
                _monospaceFamilies = names.ToList();
            }
            return _monospaceFamilies;
        }
    }

    /// <summary>
    /// Ensures the family list is ready without blocking the caller's thread. Used by the
    /// settings page so its first build never enumerates fonts on the UI thread; concurrent
    /// callers share one background probe. Returns immediately when the list is cached.
    /// </summary>
    internal static Task<IReadOnlyList<string>> EnsureMonospaceAsync(string bundledFamily)
    {
        lock (Gate)
        {
            if (_monospaceFamilies is not null)
                return Task.FromResult<IReadOnlyList<string>>(_monospaceFamilies);
            return _pendingFamilies ??= Task.Run(() => MonospaceFamilies(bundledFamily));
        }
    }

    /// <summary>
    /// Drops the cached set and family list so the next Get re-enumerates. Called on a config
    /// reload, the way Windows Terminal invalidates its FontCache when settings change.
    /// </summary>
    internal static void Invalidate()
    {
        lock (Gate)
        {
            _set?.Dispose();
            _set = null;
            _monospaceFamilies = null;
            _pendingFamilies = null;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern int AddFontResourceEx(string file, uint flags, IntPtr reserved);
}
