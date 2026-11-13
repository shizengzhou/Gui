using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// The "+new-tab" action: asks a running Gui window to open a tab, the way ghostty's own
/// +new-tab does over native IPC. The tab can start in another directory, run another
/// command, or carry a title override; everything else comes from the window's config.
/// </summary>
public static class NewTab
{
    public const string Help =
        "Usage: gui +new-tab [options] [--] [-e command...]\n" +
        "\n" +
        "Open a new tab in the running Gui window; when nothing is running, start the app\n" +
        "with that tab. Without --working-directory the directory this command runs in is\n" +
        "used, the same as ghostty.\n" +
        "\n" +
        "Options:\n" +
        "  --working-directory=DIR  directory for the new shell; relative paths resolve against\n" +
        "                           the current directory, \"~\" expands, and \"inherit\" keeps\n" +
        "                           the app's configured directory\n" +
        "  --command=CMD            command to run instead of the configured shell\n" +
        "  -e CMD [ARGS...]         same as --command, taking every following argument\n" +
        "  --title=TITLE            title override for the new tab\n" +
        "  --class=NAME             instance to talk to (default: " + IpcProtocol.DefaultClass + ")\n" +
        "  --surface-id=N           accepted for parity; only 0 (the focused surface) is valid\n" +
        "  -h, --help               show this help\n" +
        "\n" +
        "Exit status is 0 when the tab was opened, 1 when no instance answered or the app\n" +
        "rejected the request.\n";

    /// <summary>A parsed command line. Pure, so the tests can check it without a pipe.</summary>
    internal sealed class Options
    {
        public string? Class;
        public string? WorkingDirectory;
        public string? Command;
        public string? Title;
        public bool Help;
        public string? Error;
    }

    internal static Options Parse(IReadOnlyList<string> args, string cwd)
    {
        var options = new Options();
        string? command = null;
        bool directorySeen = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];

            if (arg == "-h" || arg == "--help")
            {
                options.Help = true;
                return options;
            }

            if (arg == "-e")
            {
                if (i + 1 >= args.Count)
                {
                    options.Error = "-e needs a command";
                    return options;
                }

                command = JoinCommand(args, i + 1);
                break;
            }

            if (arg == "--")
            {
                // Everything after "--" is part of the command, ghostty-style.
                if (i + 1 < args.Count) command = JoinCommand(args, i + 1);
                break;
            }

            if (TryValue(arg, args, ref i, "--class", out string? appClass))
            {
                if (appClass is null) return Missing(options, "--class");
                options.Class = appClass;
                continue;
            }

            if (TryValue(arg, args, ref i, "--surface-id", out string? surface))
            {
                if (surface is null) return Missing(options, "--surface-id");
                if (surface.Trim() != "0")
                {
                    options.Error = "--surface-id is accepted for parity only; 0 (the focused surface) is the only value";
                    return options;
                }

                continue;
            }

            if (TryValue(arg, args, ref i, "--working-directory", out string? directory))
            {
                if (directory is null) return Missing(options, "--working-directory");
                directorySeen = true;
                if (directory.Equals("inherit", StringComparison.OrdinalIgnoreCase))
                {
                    options.WorkingDirectory = null;
                }
                else
                {
                    string? resolved = ResolveDirectory(directory, cwd);
                    if (resolved is null)
                    {
                        options.Error = $"cannot resolve working directory '{directory}'";
                        return options;
                    }

                    options.WorkingDirectory = resolved;
                }

                continue;
            }

            if (TryValue(arg, args, ref i, "--command", out string? value))
            {
                if (value is null) return Missing(options, "--command");
                command = value;
                continue;
            }

            if (TryValue(arg, args, ref i, "--title", out string? title))
            {
                if (title is null) return Missing(options, "--title");
                options.Title = title;
                continue;
            }

            options.Error = $"unknown option '{arg}'";
            return options;
        }

        if (command is not null && command.Length > 0) options.Command = command;

        // ghostty sends the directory the command was run from when nothing was asked for;
        // an explicit "inherit" keeps the app's configured directory instead.
        if (!directorySeen) options.WorkingDirectory = cwd;

        return options;
    }

    private static Options Missing(Options options, string name)
    {
        options.Error = $"{name} needs a value";
        return options;
    }

    /// <summary>Matches "--name=value" and "--name value".</summary>
    private static bool TryValue(string arg, IReadOnlyList<string> args, ref int index, string name,
        out string? value)
    {
        value = null;
        if (arg.Equals(name, StringComparison.Ordinal))
        {
            if (index + 1 >= args.Count) return true;
            value = args[++index];
            return true;
        }

        string prefix = name + "=";
        if (!arg.StartsWith(prefix, StringComparison.Ordinal)) return false;
        value = arg[prefix.Length..];
        return true;
    }

    private static string JoinCommand(IReadOnlyList<string> args, int start)
    {
        var text = new StringBuilder();
        for (int i = start; i < args.Count; i++)
        {
            if (text.Length > 0) text.Append(' ');
            string arg = args[i];
            // Keep arguments with spaces together; the app parses this back as a command line.
            text.Append(arg.Contains(' ') && !arg.StartsWith('"') ? "\"" + arg + "\"" : arg);
        }

        return text.ToString();
    }

    /// <summary>Expands "~", maps "home", and makes a relative path absolute against the cwd.</summary>
    internal static string? ResolveDirectory(string value, string cwd)
    {
        string text = value.Trim();
        if (text.Length == 0) return null;

        if (text.Equals("home", StringComparison.OrdinalIgnoreCase))
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (text == "~")
            text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        else if (text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
            text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text[2..]);

        try
        {
            return Path.GetFullPath(text, cwd);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Connects, sends the request and prints the app's answer.</summary>
    public static int Run(string[] args)
    {
        Options options = Parse(args, Environment.CurrentDirectory);

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

        var request = new IpcProtocol.Request
        {
            Action = "new-tab",
            WorkingDirectory = options.WorkingDirectory,
            Command = options.Command,
            Title = options.Title,
        };

        string pipeName = IpcProtocol.PipeName(options.Class);
        IpcProtocol.Response? response = Send(pipeName, request);
        if (response is null)
        {
            // Nothing is listening: open a window instead, the way "wt new-tab" does. The
            // window runs the command as its first tab, because there is no IPC yet to add
            // one to.
            if (Launch(options)) return 0;
            Console.Error.WriteLine($"gui: no running instance of Gui answered on '{pipeName}'");
            return 1;
        }

        if (!response.Ok)
        {
            Console.Error.WriteLine($"gui: {response.Error ?? "the app rejected the request"}");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Starts Gui.exe with the same request when nothing answered the pipe. The app opens the
    /// window with the requested command as its first tab: "gui +new-tab -e foo" therefore
    /// works whether or not the terminal was already running, like "wt new-tab".
    /// </summary>
    private static bool Launch(Options options)
    {
        // The CLI sits in bin; the app is one level up.
        string exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Gui.exe"));
        if (!File.Exists(exe)) return false;

        // ShellExecute on purpose: started through CreateProcess the app would inherit this
        // console's handles, and a pane that falls back to them writes its output here instead
        // of into the terminal.
        var arguments = new StringBuilder();
        if (!string.IsNullOrEmpty(options.Command))
        {
            arguments.Append("-e \"").Append(options.Command!.Replace("\"", "\\\"")).Append('"');
        }
        if (!string.IsNullOrEmpty(options.WorkingDirectory))
        {
            if (arguments.Length > 0) arguments.Append(' ');
            arguments.Append("--working-directory=\"").Append(options.WorkingDirectory).Append('"');
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Arguments = arguments.ToString(),
            });
            return true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"gui: could not start {exe}: {error.Message}");
            return false;
        }
    }

    private static IpcProtocol.Response? Send(string pipeName, IpcProtocol.Request request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(2000);

            byte[] payload = Encoding.UTF8.GetBytes(IpcProtocol.Serialize(request) + "\n");
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();

            using var reader = new StreamReader(pipe, Encoding.UTF8);
            string? line = reader.ReadLine();
            return line is null ? null : IpcProtocol.ParseResponse(line);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
