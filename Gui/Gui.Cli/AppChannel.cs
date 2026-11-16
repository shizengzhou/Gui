using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// The two things every action needs: hand a request to the running app over its pipe, and
/// start Gui.exe when nothing answers. Kept in one place so "+new-tab" and the launch flags
/// cannot disagree about how a request is framed or how the app is started.
/// </summary>
internal static class AppChannel
{
    /// <summary>Sends one request and waits for the answer. Null means nothing was listening.</summary>
    internal static IpcProtocol.Response? Send(string pipeName, IpcProtocol.Request request)
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

    /// <summary>
    /// True when a Gui is listening on the pipe. The connection is closed without sending a
    /// request, which the server reports as an empty request and only logs.
    /// </summary>
    internal static bool IsRunning(string pipeName)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(300);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts Gui.exe with the given arguments. ShellExecute on purpose: started through
    /// CreateProcess the app would inherit this console's handles, and a pane that falls back
    /// to them writes its output here instead of into the terminal.
    /// </summary>
    internal static bool Start(string arguments)
    {
        // The CLI sits in bin with the app one level up. Looking next to the CLI first would
        // find the CLI itself: its apphost is called gui.exe, and Windows matches that name
        // case-insensitively against Gui.exe.
        string exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Gui.exe"));
        if (!File.Exists(exe)) return false;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Arguments = arguments,
            });
            return true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"gui: could not start {exe}: {error.Message}");
            return false;
        }
    }
}
