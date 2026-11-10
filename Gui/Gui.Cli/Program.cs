using System;
using System.Reflection;
using System.Text;

namespace Gui.Cli;

/// <summary>
/// Entry point for the companion CLI. Actions are prefixed with '+', the same shape
/// ghostty (and kitty before it) uses for special entry points. The dispatcher is kept
/// deliberately small: adding a new action is one case below plus its own file.
/// </summary>
internal static class Program
{
    private const string Usage =
        "Gui command-line companion\n" +
        "\n" +
        "Usage:\n" +
        "  gui-cli +<action> [options]\n" +
        "\n" +
        "Actions (prefixed with '+', like ghostty):\n" +
        "  +show-config    print the effective configuration in Ghostty format\n" +
        "  +list-keybinds  print the key bindings this build honors, as Ghostty keybind lines\n" +
        "  +new-tab        open a new tab in the running window\n" +
        "  +help           list the available actions\n" +
        "  +version        print the version\n" +
        "\n" +
        "Run 'gui-cli +show-config --help' for that action's options.\n" +
        "This tool ships next to Gui.exe; add that folder to PATH to call it from any shell.\n";

    private static int Main(string[] args)
    {
        // Emit UTF-8 without a BOM and with ghostty-style LF line endings so the output
        // can be redirected into a config file verbatim.
        Console.OutputEncoding = new UTF8Encoding(false);

        if (args.Length == 0)
        {
            Console.Out.Write(Usage);
            return 0;
        }

        string action = args[0];
        string[] rest = args[1..];

        switch (action)
        {
            case "+show-config":
                return ShowConfig.Run(rest);

            case "+list-keybinds":
                return ListKeybinds.Run(rest);

            case "+new-tab":
                return NewTab.Run(rest);

            case "+help":
            case "--help":
            case "-h":
            case "help":
                Console.Out.Write(Usage);
                return 0;

            case "+version":
            case "--version":
            case "-V":
            case "version":
                Console.Out.WriteLine("gui-cli " + VersionString());
                return 0;

            default:
                if (action.StartsWith("+", StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"gui-cli: unknown action '{action}'");
                    Console.Error.WriteLine("Run 'gui-cli +help' to list the available actions.");
                    return 2;
                }

                Console.Error.WriteLine($"gui-cli: expected a +action, got '{action}'");
                Console.Error.Write(Usage);
                return 2;
        }
    }

    private static string VersionString()
    {
        string? version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(version)) return "dev";
        int plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
