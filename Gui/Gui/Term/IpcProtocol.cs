using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gui.Term;

/// <summary>
/// The local control channel the CLI talks to: ghostty's "+new-tab" over native IPC, reduced
/// to one request line and one response line of UTF-8 JSON over a named pipe.
///
/// Both ends compile this file (the app and Gui.Cli link it), so the pipe name and the wire
/// shape cannot drift. Nothing secret goes over it: a request only ever asks the running app
/// to open a tab or come to the front, and the pipe accepts the current user's processes only.
/// </summary>
public static class IpcProtocol
{
    /// <summary>Application class, the ghostty "class" equivalent used in the pipe name.</summary>
    public const string DefaultClass = "Gui";

    /// <summary>
    /// Per-user, per-session pipe name, so two signed-in users (or two sessions) cannot reach
    /// each other's window and a stale server from another session is never found.
    /// </summary>
    public static string PipeName(string? appClass = null)
    {
        string cls = Sanitize(string.IsNullOrWhiteSpace(appClass) ? DefaultClass : appClass);
        string user = Environment.UserName.ToLowerInvariant();
        int session;
        try
        {
            session = Process.GetCurrentProcess().SessionId;
        }
        catch (Exception)
        {
            session = 0;
        }

        return $"gui-terminal-{cls}-{user}-{session}";
    }

    /// <summary>A pipe name cannot carry separators or spaces, so unusual classes are folded.</summary>
    private static string Sanitize(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (char c in value)
            text.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        return text.ToString();
    }

    public static string Serialize(Request request) =>
        JsonSerializer.Serialize(request, IpcJsonContext.Default.Request);

    public static string Serialize(Response response) =>
        JsonSerializer.Serialize(response, IpcJsonContext.Default.Response);

    public static Request? ParseRequest(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, IpcJsonContext.Default.Request);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static Response? ParseResponse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, IpcJsonContext.Default.Response);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One command for the running app. Unknown fields are ignored.</summary>
    public sealed class Request
    {
        /// <summary>"new-tab", "focus" or "render-sample".</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>Directory the new tab's shell starts in; null keeps the app's default.</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>Command to run instead of the configured shell.</summary>
        public string? Command { get; set; }

        /// <summary>Title override for the new tab.</summary>
        public string? Title { get; set; }
        /// <summary>Terminal session a "focus" request should select; 0 leaves the current one alone.</summary>
        public int Session { get; set; }
        /// <summary>Font family for "render-sample".</summary>
        public string? Family { get; set; }
        /// <summary>Cell size in physical pixels for "render-sample".</summary>
        public int CellWidth { get; set; }
        public int CellHeight { get; set; }
        /// <summary>How many cells wide the preview pane is, for "render-sample".</summary>
        public int Columns { get; set; }
        /// <summary>Foreground colour as #RRGGBB for "render-sample".</summary>
        public string? Foreground { get; set; }
        /// <summary>Where "render-sample" writes its PNG.</summary>
        public string? Output { get; set; }
    }

    /// <summary>The app's answer: ok, or a message the CLI prints on stderr.</summary>
    public sealed class Response
    {
        public bool Ok { get; set; }

        public string? Error { get; set; }
    }
}

/// <summary>
/// The wire shape, generated at compile time. The CLI ships trimmed and single-file, where
/// reflection-based System.Text.Json is disabled by default and its metadata can be trimmed
/// away; a generated context removes both hazards and lets the analyzer prove the shape
/// instead of reporting IL2026. The options reproduce the reflection setup this replaced:
/// camelCase names and null members omitted.
///
/// It sits outside <see cref="IpcProtocol"/> rather than inside it: a nested context requires
/// every containing type to be partial (SYSLIB1032), and there is no reason to make the public
/// IPC class partial just to host generated code.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IpcProtocol.Request))]
[JsonSerializable(typeof(IpcProtocol.Response))]
internal partial class IpcJsonContext : JsonSerializerContext;
