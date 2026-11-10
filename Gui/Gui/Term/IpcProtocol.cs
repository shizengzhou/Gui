using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gui.Term;

/// <summary>
/// The local control channel gui-cli talks to: ghostty's "+new-tab" over native IPC, reduced
/// to one request line and one response line of UTF-8 JSON over a named pipe.
///
/// Both ends compile this file (the app and Gui.Cli link it), so the pipe name and the wire
/// shape cannot drift. Nothing secret goes over it: a request only ever asks the running app
/// to open a tab, and the pipe accepts the current user's processes only.
/// </summary>
public static class IpcProtocol
{
    /// <summary>Application class, the ghostty "class" equivalent used in the pipe name.</summary>
    public const string DefaultClass = "Gui";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

    public static string Serialize(Request request) => JsonSerializer.Serialize(request, Json);

    public static string Serialize(Response response) => JsonSerializer.Serialize(response, Json);

    public static Request? ParseRequest(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<Request>(line, Json);
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
            return JsonSerializer.Deserialize<Response>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One command for the running app. Unknown fields are ignored.</summary>
    public sealed class Request
    {
        /// <summary>Currently only "new-tab".</summary>
        public string Action { get; set; } = string.Empty;

        /// <summary>Directory the new tab's shell starts in; null keeps the app's default.</summary>
        public string? WorkingDirectory { get; set; }

        /// <summary>Command to run instead of the configured shell.</summary>
        public string? Command { get; set; }

        /// <summary>Title override for the new tab.</summary>
        public string? Title { get; set; }
    }

    /// <summary>The app's answer: ok, or a message the CLI prints on stderr.</summary>
    public sealed class Response
    {
        public bool Ok { get; set; }

        public string? Error { get; set; }
    }
}
