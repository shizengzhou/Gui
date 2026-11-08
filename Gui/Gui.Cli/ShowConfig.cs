using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// The "+show-config" action: prints the effective configuration in Ghostty's own
/// "key = value" format so the output can be saved back or diffed. Mirrors ghostty's
/// +show-config: by default only keys that were set are shown, --default prints the full
/// option surface with empty (defaulting) values, and --docs annotates each key.
/// </summary>
public static class ShowConfig
{
    public const string Help =
        "Usage: gui-cli +show-config [options]\n" +
        "\n" +
        "Print the configuration in Ghostty format. By default only the keys set in the\n" +
        "config files (merged across config-file includes) are printed.\n" +
        "\n" +
        "Options:\n" +
        "  --default          print every recognized option with its (empty) default value,\n" +
        "                     without reading the user config\n" +
        "  --full             also print options that are not set (as empty values)\n" +
        "  --docs             print a short description as a # comment above each option\n" +
        "  --changes-only     only print changed keys (the default; accepted for parity)\n" +
        "  --no-pager         accepted for parity; this tool always writes straight to stdout\n" +
        "  --config-file PATH read PATH instead of %APPDATA%\\gui\\config\n" +
        "  -h, --help         show this help\n";

    /// <summary>
    /// Renders the config as text. Pure and console-free so it can be unit tested by
    /// linking this file into the test project.
    /// </summary>
    /// <param name="config">The parsed configuration (raw merged values).</param>
    /// <param name="includeAll">Emit every known key, using an empty value when unset.</param>
    /// <param name="docs">Emit a summary comment above each key.</param>
    public static string Render(Config config, bool includeAll, bool docs)
    {
        var sb = new StringBuilder();
        if (docs && includeAll)
            sb.Append("# Every recognized option. An empty value means the default is used.\n");

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ConfigOption option in EnumerateOptions())
        {
            config.Values.TryGetValue(option.Name, out List<string>? values);
            bool hasValues = values is { Count: > 0 };
            if (!hasValues && !includeAll) continue;

            if (docs) AppendDocs(sb, option);

            if (hasValues)
            {
                foreach (string value in values!)
                    sb.Append(option.Name).Append(" = ").Append(value).Append('\n');
            }
            else
            {
                // Ghostty prints "key =" for a defaulting key; keep that shape so the
                // output is a valid config file with a place to hang the doc comment.
                sb.Append(option.Name).Append(" =\n");
            }
            written.Add(option.Name);
        }

        // Defensive: a key accepted by the parser but missing from the schema arrays (a
        // newly added option) must not be silently dropped.
        foreach (KeyValuePair<string, List<string>> pair in config.Values)
        {
            if (written.Contains(pair.Key)) continue;
            foreach (string value in pair.Value)
                sb.Append(pair.Key).Append(" = ").Append(value).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Parses the flags, loads the config and writes the result to stdout.</summary>
    public static int Run(string[] args)
    {
        bool showDefault = false;
        bool docs = false;
        bool full = false;
        bool help = false;
        string? configFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--default": showDefault = true; break;
                case "--docs": docs = true; break;
                case "--full": full = true; break;
                // Ghostty defaults to changes-only; we already are, so nothing to do.
                case "--changes-only": break;
                // This tool never pages, so the flag is a no-op.
                case "--no-pager": break;
                case "-h":
                case "--help": help = true; break;
                case "--config-file":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("gui-cli: --config-file needs a path");
                        return 2;
                    }
                    configFile = args[i];
                    break;
                default:
                    const string prefix = "--config-file=";
                    if (arg.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        configFile = arg[prefix.Length..];
                    }
                    else
                    {
                        Console.Error.WriteLine($"gui-cli: unknown option '{arg}' for +show-config");
                        Console.Error.Write(Help);
                        return 2;
                    }
                    break;
            }
        }

        if (help)
        {
            Console.Out.Write(Help);
            return 0;
        }

        if (configFile is not null && !File.Exists(configFile))
            Console.Error.WriteLine($"gui-cli: config file not found: {configFile}");

        string file = configFile ?? Config.ConfigFile();
        // --default ignores the user's config entirely, like ghostty.
        Config config = showDefault ? new Config() : Config.LoadRaw(file);
        Console.Out.Write(Render(config, showDefault || full, docs));
        return 0;
    }

    /// <summary>Stable order: Ghostty's own schema first, then this app's extra keys.</summary>
    private static IEnumerable<ConfigOption> EnumerateOptions()
    {
        foreach (ConfigOption option in ConfigSchema.Ghostty) yield return option;
        foreach (ConfigOption option in ConfigSchema.Application) yield return option;
    }

    private static void AppendDocs(StringBuilder sb, ConfigOption option)
    {
        sb.Append("# ").Append(option.Summary).Append('\n');
        if (ConfigSchema.IsForeign(option))
        {
            string platform = option.Platform == ConfigPlatform.MacOS ? "macOS" : "Linux";
            sb.Append("# ").Append(platform).Append(" only; ignored on Windows\n");
        }
    }
}
