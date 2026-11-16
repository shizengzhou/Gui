using System;
using System.Collections.Generic;
using System.Text;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// The "+list-fonts" action. On a terminal it opens the font browser (tsp's layout: a list on
/// the left, a preview rendered from the family's own outlines on the right, Enter writing
/// "font-family" into the config). Redirected, it prints the families one per line so a script
/// can read them, the way "+list-keybinds" turns into config lines when piped.
/// </summary>
public static class ListFonts
{
    public const string Help =
        "Usage: gui +list-fonts [options]\n" +
        "\n" +
        "Browse the installed font families. On a terminal this opens the font browser: the\n" +
        "list on the left, a preview of the highlighted family on the right, rendered from\n" +
        "that font's real outlines when the terminal accepts images (kitty graphics, which\n" +
        "Gui itself does). Enter writes \"font-family\" into the config.\n" +
        "\n" +
        "Options:\n" +
        "  --family=<text>      only families whose name contains text\n" +
        "  --styles             add each family's styles (plain listing only)\n" +
        "  --plain              print the list even on a terminal\n" +
        "  --images=auto|always|never\n" +
        "                       draw the preview image; auto asks TERM_PROGRAM (default)\n" +
        "  -h, --help           show this help\n" +
        "\n" +
        "Keys: up/down move, / starts the filter (typing filters too), Enter applies, Esc\n" +
        "clears the filter, q quits.\n";

    public static int Run(string[] args)
    {
        string query = string.Empty;
        string images = "auto";
        bool styles = false;
        bool plain = false;

        foreach (string arg in args)
        {
            if (arg is "-h" or "--help")
            {
                Console.Out.Write(Help);
                return 0;
            }
            if (arg == "--styles")
            {
                styles = true;
                continue;
            }
            if (arg == "--plain")
            {
                plain = true;
                continue;
            }
            if (arg.StartsWith("--family=", StringComparison.Ordinal))
            {
                query = arg["--family=".Length..];
                continue;
            }
            if (arg.StartsWith("--images=", StringComparison.Ordinal))
            {
                images = arg["--images=".Length..].ToLowerInvariant();
                if (images is not ("auto" or "always" or "never"))
                {
                    Console.Error.WriteLine($"gui: --images expects auto, always or never, got '{images}'");
                    return 1;
                }
                continue;
            }

            Console.Error.WriteLine($"gui: unknown option '{arg}'");
            Console.Error.WriteLine("Run 'gui +list-fonts --help' for usage.");
            return 1;
        }

        List<FontFamilyInfo> families = FontCatalog.Filter(FontCatalog.FromRegistry(), query);
        if (families.Count == 0)
        {
            Console.Error.WriteLine(query.Length == 0
                ? "gui: no installed fonts found"
                : $"gui: no font family matches '{query}'");
            return 1;
        }

        bool browse = !plain && !Console.IsOutputRedirected && !Console.IsInputRedirected;
        if (browse) return FontBrowser.Run(families, CurrentFamily(), UseImages(images), Foreground());

        var text = new StringBuilder();
        foreach (FontFamilyInfo family in families)
        {
            text.Append(family.Name);
            if (styles) text.Append("  (").Append(string.Join(", ", family.Styles())).Append(')');
            text.Append('\n');
        }
        Console.Out.Write(text.ToString());
        return 0;
    }

    /// <summary>
    /// The colour the sample is drawn in: the config's "foreground" when it sets one, else the
    /// light text the Gui themes use. The card itself is transparent, so the sample sits on
    /// whatever background the terminal paints.
    /// </summary>
    private static (byte r, byte g, byte b) Foreground()
    {
        try
        {
            Config config = Config.LoadRaw(Config.ConfigFile());
            if (config.Values.TryGetValue("foreground", out List<string>? values) && values.Count > 0 &&
                Config.TryParseColor(values[^1], out Rgb rgb))
            {
                return (rgb.R, rgb.G, rgb.B);
            }
        }
        catch (Exception)
        {
            // The fallback colour is the theme's light text either way.
        }
        return (245, 245, 247);
    }

    /// <summary>The family the config currently names, so the browser starts on it.</summary>
    private static string CurrentFamily()
    {
        try
        {
            Config config = Config.LoadRaw(Config.ConfigFile());
            if (config.Values.TryGetValue("font-family", out List<string>? values) && values.Count > 0)
                return values[^1];
        }
        catch (Exception)
        {
            // An unreadable config only costs the starting position.
        }
        return string.Empty;
    }

    /// <summary>
    /// Whether the preview may draw an image. Only the kitty graphics protocol is emitted, so
    /// the terminals known to speak it answer yes; anyone else gets the text preview instead of
    /// a screen full of escape codes.
    /// </summary>
    private static bool UseImages(string mode) => mode switch
    {
        "always" => true,
        "never" => false,
        _ => SupportsImages(),
    };

    private static bool SupportsImages()
    {
        string program = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? string.Empty;
        if (program.Contains("gui", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KITTY_WINDOW_ID"))) return true;
        string term = Environment.GetEnvironmentVariable("TERM") ?? string.Empty;
        return term.Contains("kitty", StringComparison.OrdinalIgnoreCase);
    }
}
