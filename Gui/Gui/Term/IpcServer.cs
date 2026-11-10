using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace Gui.Term;

/// <summary>
/// The app end of gui-cli's control channel: one named pipe, one request at a time, each
/// request handed to the UI thread so it runs exactly like a palette row.
///
/// The server lives for the whole process and is started once the first window exists; a
/// failure to create the pipe (another session owning the name, a locked-down policy) only
/// costs the "+new-tab" command, so it is logged and swallowed.
/// </summary>
internal sealed class IpcServer : IDisposable
{
    private readonly string _pipeName;
    private readonly DispatcherQueue _queue;
    private readonly Func<IpcProtocol.Request, string?> _handle;
    private readonly CancellationTokenSource _cancel = new();
    private NamedPipeServerStream? _waiting;

    private IpcServer(string pipeName, DispatcherQueue queue, Func<IpcProtocol.Request, string?> handle)
    {
        _pipeName = pipeName;
        _queue = queue;
        _handle = handle;
    }

    internal static IpcServer? Start(string pipeName, DispatcherQueue queue,
        Func<IpcProtocol.Request, string?> handle)
    {
        var server = new IpcServer(pipeName, queue, handle);
        try
        {
            _ = server.LoopAsync();
            Diag.Log("ipc", $"listening on {pipeName}");
            return server;
        }
        catch (Exception error)
        {
            Diag.Warn("ipc", $"server did not start: {error.Message}");
            server.Dispose();
            return null;
        }
    }

    private async Task LoopAsync()
    {
        while (!_cancel.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception error)
            {
                Diag.Warn("ipc", $"pipe unavailable: {error.Message}");
                return;
            }

            _waiting = pipe;
            try
            {
                await pipe.WaitForConnectionAsync(_cancel.Token).ConfigureAwait(false);

                string? line = await ReadLineAsync(pipe, _cancel.Token).ConfigureAwait(false);
                IpcProtocol.Response reply = await ExecuteAsync(line).ConfigureAwait(false);

                byte[] bytes = Encoding.UTF8.GetBytes(IpcProtocol.Serialize(reply) + "\n");
                await pipe.WriteAsync(bytes, _cancel.Token).ConfigureAwait(false);
                await pipe.FlushAsync(_cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                return;
            }
            catch (Exception error)
            {
                // A client that disconnects mid-request is normal (the CLI was interrupted).
                Diag.Debug("ipc", $"request failed: {error.Message}");
            }
            finally
            {
                _waiting = null;
                pipe.Dispose();
            }
        }
    }

    /// <summary>
    /// Parses one line and runs it on the UI thread: creating a tab touches XAML, so it has
    /// to happen where the palette's own rows run.
    /// </summary>
    private async Task<IpcProtocol.Response> ExecuteAsync(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return new IpcProtocol.Response { Ok = false, Error = "empty request" };

        IpcProtocol.Request? request = IpcProtocol.ParseRequest(line);
        if (request is null)
            return new IpcProtocol.Response { Ok = false, Error = "malformed request" };

        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryEnqueue(() =>
            {
                try
                {
                    done.SetResult(_handle(request));
                }
                catch (Exception error)
                {
                    Diag.LogException("ipc", error);
                    done.SetResult(error.Message);
                }
            }))
        {
            return new IpcProtocol.Response { Ok = false, Error = "app is shutting down" };
        }

        string? failure = await done.Task.ConfigureAwait(false);
        return new IpcProtocol.Response { Ok = failure is null, Error = failure };
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancel)
    {
        var buffer = new byte[1];
        var text = new StringBuilder();
        while (text.Length < 4096)
        {
            int read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer[0] == (byte)'\n') return text.ToString();
            text.Append((char)buffer[0]);
        }

        return text.Length == 0 ? null : text.ToString();
    }

    public void Dispose()
    {
        _cancel.Cancel();
        _waiting?.Dispose();
        _cancel.Dispose();
    }
}
