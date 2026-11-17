using System;
using System.Collections.Generic;
using System.Text;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// The "+list-themes" action. On a terminal it opens the theme browser (tsp's layout: All/Light/
/// Dark chips, the themes in a column, the highlighted theme's colors and a syntax sample on the
/// right, Enter writing the matching half of "theme" into the config). When the output is
/// redirected it prints the themes one per line, so the same command can be read by a script.
/// </summary>
public static class ListThemes
{
    public const string Help =
        "Usage: gui +list-themes\n" +
        "\n" +
        "Browse the Ghostty themes bundled with Gui and the ones in the \"themes\" folder\n" +
        "beside the config file (a theme there replaces the bundled one of the same name).\n" +
        "\n" +
        "On a terminal this opens the browser: the All/Light/Dark chips at the top, the\n" +
        "themes in a column with their brightness, and the highlighted theme painted on the\n" +
        "right — its 16 ANSI colors, a shell sample, and a code sample that shows its\n" +
        "selection and cursor colors. Enter writes the theme into the config, only touching\n" +
        "the light: or dark: half that matches it.\n" +
        "\n" +
        "When the output is redirected the themes are printed one per line instead.\n" +
        "\n" +
        "Keys: j/k or up/down move, tab (shift+tab) switches All/Light/Dark, / opens the\n" +
        "filter, Enter applies, Esc clears the filter, q quits.\n";

    public static int Run(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg is "-h" or "--help")
            {
                Console.Out.Write(Help);
                return 0;
            }

            Console.Error.WriteLine($"gui: unexpected argument '{arg}'");
            Console.Error.WriteLine("Run 'gui +list-themes --help' for usage.");
            return 1;
        }

        string? directory = ThemeCatalog.FindDirectory();
        if (directory is null)
        {
            Console.Error.WriteLine("gui: the bundled themes were not found next to Gui");
            return 1;
        }

        List<ThemeEntry> themes = ThemeCatalog.Load(directory, TerminalThemes.UserDirectory());
        if (themes.Count == 0)
        {
            Console.Error.WriteLine($"gui: no themes found in {directory}");
            return 1;
        }

        bool browse = !Console.IsOutputRedirected && !Console.IsInputRedirected;
        if (browse) return ThemeBrowser.Run(themes, directory, CurrentThemeValue());

        var text = new StringBuilder();
        foreach (ThemeEntry theme in themes)
        {
            text.Append(theme.Name);
            text.Append(theme.Light ? "  (light)" : "  (dark)");
            text.Append('\n');
        }
        Console.Out.Write(text.ToString());
        return 0;
    }

    /// <summary>The "theme" value the config currently sets, so the browser can merge into it.</summary>
    private static string CurrentThemeValue()
    {
        try
        {
            Config config = Config.LoadRaw(Config.ConfigFile());
            if (config.Values.TryGetValue("theme", out List<string>? values) && values.Count > 0)
                return values[^1];
        }
        catch (Exception)
        {
            // An unreadable config only costs the merge base; the default is two design names.
        }

        return Config.DefaultTheme;
    }
}
