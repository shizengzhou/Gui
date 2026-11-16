using System;
using System.Collections.Generic;
using System.Text;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// Ghostty's command-line configuration overrides: "gui --background=282c34 --font-size=18"
/// opens a window with those values layered on top of %APPDATA%\gui\config. The keys and
/// values are checked here first, with the schema and the rules the config file itself goes
/// through, so a typo is reported in the shell that typed it instead of in the app's log.
/// </summary>
internal static class Launch
{
    public const string Help =
        "Usage: gui --<key>=<value> [--<key>=<value> ...]\n" +
        "\n" +
        "Open a window with configuration values layered on top of the config file, the way\n" +
        "ghostty treats its own command-line flags. Every key from +show-config is accepted:\n" +
        "\n" +
        "  gui --background=282c34 --font-size=18\n" +
        "  gui --font-family=\"JetBrainsMono Nerd Font\" --window-theme=dark\n" +
        "\n" +
        "A repeatable key (font-family, env, keybind ...) appends to what the file set; an\n" +
        "empty value resets the key. An unknown key or a bad value exits with status 2 instead\n" +
        "of starting anything. The values belong to the process this command starts, so it\n" +
        "always opens a new instance; a Gui that is already running is left untouched.\n" +
        "\n" +
        "  -h, --help               show this help\n";

    /// <summary>A parsed command line. Pure, so the tests can check it without a pipe.</summary>
    internal sealed class Options
    {
        public List<string> Overrides = new();
        public bool Help;
        public string? Error;
    }

    internal static Options Parse(IReadOnlyList<string> args)
    {
        var options = new Options();

        foreach (string arg in args)
        {
            if (arg is "-h" or "--help")
            {
                options.Help = true;
                return options;
            }

            // Ghostty's spelling only: "--key=value". A bare "--key" would be ambiguous with
            // the options of the +actions, which take a separate value.
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.IndexOf('=') < 3)
            {
                options.Error = $"expected --key=value, got '{arg}'";
                return options;
            }

            string entry = arg[2..];
            if (!Config.ValidateOverride(entry, out string error))
            {
                options.Error = error;
                return options;
            }

            options.Overrides.Add(entry);
        }

        if (options.Overrides.Count == 0) options.Error = "no configuration values given";
        return options;
    }

    public static int Run(string[] args)
    {
        Options options = Parse(args);

        if (options.Help)
        {
            Console.Out.Write(Help);
            return 0;
        }

        if (options.Error is not null)
        {
            Console.Error.WriteLine($"gui: {options.Error}");
            Console.Error.Write(Help);
            return 2;
        }

        // A launch flag belongs to the process it starts, the way the config file does: the
        // palette is resolved once per process, so attaching to a running window could not
        // honor it. This therefore always opens a new instance, like launching Gui.exe with
        // the same arguments.
        string arguments = Arguments(options.Overrides);

        // A Gui that is already running owns the saved session; a scratch instance must not
        // replace it when its windows close.
        if (AppChannel.IsRunning(IpcProtocol.PipeName())) arguments += " --no-session-save";

        if (AppChannel.Start(arguments)) return 0;

        Console.Error.WriteLine("gui: could not start Gui.exe");
        return 1;
    }

    /// <summary>
    /// The flags as a Windows command line. Values with spaces are quoted so Gui.exe receives
    /// them as one argument, the same shape a config line would have written them in.
    /// </summary>
    internal static string Arguments(IReadOnlyList<string> overrides)
    {
        var text = new StringBuilder();
        foreach (string entry in overrides)
        {
            if (text.Length > 0) text.Append(' ');

            int separator = entry.IndexOf('=');
            text.Append("--").Append(entry, 0, separator).Append('=');

            string value = entry[(separator + 1)..];
            if (value.Contains(' ') || value.Contains('"'))
                text.Append('"').Append(value.Replace("\"", "\\\"")).Append('"');
            else
                text.Append(value);
        }

        return text.ToString();
    }
}
