using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Gui.Term;

/// <summary>
/// Shell integration for the shells this app can drive. Ghostty upstream ships
/// bash/zsh/fish/elvish/nushell scripts, none of which fit a Windows install, so the port
/// supplies its own: a prompt hook that emits the OSC 133 semantic marks (prompt
/// start/end, command start/finish) plus the OSC 7 working-directory report.
/// <see cref="ShellMarkParser"/> reads those marks back from the PTY stream so the app
/// knows when a command is still running.
///
/// PowerShell takes the script inline (-EncodedCommand); bash reads it from a generated
/// --rcfile. A login shell and a WSL launcher are left alone: bash ignores --rcfile with
/// --login, and the Linux side cannot open a Windows path.
/// </summary>
internal static class ShellIntegration
{
    /// <summary>
    /// Wraps a shell command line with the integration script. The command is returned
    /// unchanged when integration is off, when it names another shell, or when it already
    /// carries startup flags the integration cannot combine with.
    /// </summary>
    public static string Inject(string shell, string mode, ShellIntegrationFeatures features, string executable)
    {
        if (mode is "none") return shell;

        string stem = Stem(executable.Length > 0 ? executable : shell);
        string? arguments = ArgumentsAfter(shell, executable);

        if (stem is "pwsh" or "powershell")
        {
            if (mode is not ("detect" or "powershell" or "pwsh")) return shell;
            if (arguments != string.Empty) return shell;
            return shell + " -NoExit -EncodedCommand " + Encode(Script(features));
        }

        if (stem == "bash")
        {
            if (mode is not ("detect" or "bash")) return shell;
            if (!BashCanUseRcFile(arguments)) return shell;
            string? rc = WriteBashRc(features);
            if (rc is null) return shell;

            // --rcfile is a long option and bash only accepts those before the short ones:
            // "-i --rcfile x" makes bash read "--" as an option. It also needs -i, because a
            // pane does not look interactive to bash and only an interactive shell reads the
            // startup file.
            string trimmed = shell.Trim();
            int headLength = arguments!.Length == 0 ? trimmed.Length : trimmed.Length - arguments.Length;
            string head = trimmed[..headLength].TrimEnd();
            // Git's Windows Terminal profile runs "bash.exe -i -l", but a login shell ignores
            // --rcfile, so -l is dropped: the rc file runs /etc/profile, which is the login
            // chain, and the shell stays interactive.
            return head + " --rcfile \"" + rc + "\" -i";
        }

        return shell;
    }

    // ---------------- bash ----------------

    /// <summary>
    /// The bash template with the feature flags applied. Kept separate from the file so a
    /// test can check the script without a temporary file on disk.
    /// </summary>
    internal static string BashScript(ShellIntegrationFeatures features) =>
        BashScript(features, BashTemplate());

    internal static string BashScript(ShellIntegrationFeatures features, string template) =>
        template
            .Replace("@CURSOR@", features.Cursor ? "1" : "0", StringComparison.Ordinal)
            .Replace("@CURSOR_STEADY@", features.CursorSteady ? "1" : "0", StringComparison.Ordinal)
            .Replace("@TITLE@", features.Title ? "1" : "0", StringComparison.Ordinal);

    /// <summary>
    /// Writes the script to %TEMP%\gui and returns it as a path bash can open. The file
    /// name carries a hash of the contents, so changing a feature produces a new file
    /// instead of rewriting one a running shell may still be reading.
    /// </summary>
    private static string? WriteBashRc(ShellIntegrationFeatures features)
    {
        try
        {
            string template = BashTemplate();
            if (template.Length == 0) return null;

            string script = BashScript(features, template);
            string directory = Path.Combine(Path.GetTempPath(), "gui");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "bash-integration-" + Hash(script) + ".bash");
            if (!File.Exists(path)) File.WriteAllText(path, script, new UTF8Encoding(false));
            return path.Replace('\\', '/');
        }
        catch (Exception error)
        {
            Diag.Warn("shell-integration", "bash: " + error.Message);
            return null;
        }
    }

    /// <summary>The template next to the app, or from the source tree in a test run.</summary>
    private static string BashTemplate()
    {
        foreach (string path in BashTemplateCandidates())
        {
            try
            {
                if (!File.Exists(path)) continue;
                // bash chokes on CRLF: a stray \r becomes part of the command.
                return File.ReadAllText(path).Replace("\r\n", "\n");
            }
            catch
            {
                // Try the next candidate.
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> BashTemplateCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Assets", "shell", "bash.bash");

        // Tests run from the test output directory; walk up to the repository root.
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            yield return Path.Combine(dir.FullName, "Gui", "Gui", "Assets", "shell", "bash.bash");
    }

    /// <summary>File name stem of an executable path, without quotes or extension.</summary>
    private static string Stem(string path)
    {
        string name = path.Trim().Trim('"');
        int slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// Arguments that follow the executable, or null when the command does not name that
    /// executable at all. An auto-detected shell arrives as an unquoted path that may
    /// contain spaces, so the executable is compared as a whole rather than by token.
    /// </summary>
    private static string? ArgumentsAfter(string command, string executable)
    {
        string trimmed = command.Trim();
        // The executable may arrive quoted ("C:\Program Files\Git\bin\bash.exe") or bare, and
        // an auto-detected shell may be an unquoted path with spaces.
        string head = trimmed.StartsWith('"') ? "\"" + executable + "\"" : executable;
        if (!trimmed.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return null;
        return trimmed[head.Length..].Trim();
    }

    /// <summary>
    /// bash only reads --rcfile for a plain interactive shell: a login shell ignores it,
    /// and a command line that already picks its own startup files must not be rewritten.
    /// </summary>
    private static bool BashCanUseRcFile(string? arguments)
    {
        if (arguments is null) return false;
        foreach (string token in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Login and interactive flags are the only ones the wrapper can rewrite; anything
            // that selects its own startup files or a command (--rcfile, --norc, -c) cannot.
            if (token is not ("-i" or "--interactive" or "-l" or "--login" or "-il" or "-li"))
                return false;
        }

        return true;
    }

    private static string Hash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text) hash = unchecked((hash ^ c) * 16777619);
        return hash.ToString("x8");
    }

    private static string Encode(string script) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// The script is deliberately defensive: every optional block is wrapped so a missing
    /// PSReadLine or an unusual prompt function cannot leave the shell in a broken state.
    /// It stays compatible with both Windows PowerShell 5.1 and PowerShell 7.
    /// </summary>
    private static string Script(ShellIntegrationFeatures features)
    {
        var script = new StringBuilder(2048);
        script.AppendLine("$global:__guiPrompt = $function:prompt");
        script.AppendLine("$global:__guiStarted = $false");
        script.AppendLine("$global:__guiEsc = [char]27");
        script.AppendLine("$global:__guiBel = [char]7");
        script.AppendLine("$global:__guiCursor = $" + (features.Cursor ? "true" : "false"));
        script.AppendLine("$global:__guiCursorSteady = $" + (features.CursorSteady ? "true" : "false"));
        script.AppendLine("$global:__guiTitle = $" + (features.Title ? "true" : "false"));
        script.AppendLine();
        script.AppendLine("try {");
        script.AppendLine("  Import-Module PSReadLine -ErrorAction SilentlyContinue | Out-Null");
        script.AppendLine("  Set-PSReadLineKeyHandler -Key Enter -ScriptBlock {");
        script.AppendLine("    $line = $null");
        script.AppendLine("    $cursor = $null");
        script.AppendLine("    [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)");
        script.AppendLine("    [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()");
        script.AppendLine("    if ($line -and $line.Trim().Length -gt 0) {");
        script.AppendLine("      $global:__guiStarted = $true");
        script.AppendLine("      $esc = $global:__guiEsc");
        script.AppendLine("      $bel = $global:__guiBel");
        script.AppendLine("      if ($global:__guiCursor) {");
        script.AppendLine("        $code = if ($global:__guiCursorSteady) { 6 } else { 5 }");
        script.AppendLine("        [Console]::Write(\"$esc[$code q\")");
        script.AppendLine("      }");
        script.AppendLine("      [Console]::Write(\"$esc]133;C$bel\")");
        script.AppendLine("    }");
        script.AppendLine("  }");
        script.AppendLine("} catch {");
        script.AppendLine("}");
        script.AppendLine();
        script.AppendLine("function global:prompt {");
        script.AppendLine("  $ok = $?");
        script.AppendLine("  $exit = $global:LASTEXITCODE");
        script.AppendLine("  $status = if ($ok) { 0 } elseif ($null -ne $exit -and $exit -ne 0) { $exit } else { 1 }");
        script.AppendLine("  $esc = [char]27");
        script.AppendLine("  $bel = [char]7");
        script.AppendLine("  $out = ''");
        script.AppendLine("  if ($global:__guiStarted) {");
        script.AppendLine("    $out += \"$esc]133;D;$status$bel\"");
        script.AppendLine("    $global:__guiStarted = $false");
        script.AppendLine("  }");
        script.AppendLine("  $loc = $executionContext.SessionState.Path.CurrentLocation");
        script.AppendLine("  if ($loc.Provider.Name -eq 'FileSystem') {");
        script.AppendLine("    $path = $loc.ProviderPath");
        script.AppendLine("    $uri = $path -replace '%', '%25'");
        script.AppendLine("    $uri = $uri -replace '\\\\', '/'");
        script.AppendLine("    $out += \"$esc]7;file:///$uri$bel\"");
        script.AppendLine("    if ($global:__guiTitle) {");
        script.AppendLine("      $out += \"$esc]2;$path$bel\"");
        script.AppendLine("    }");
        script.AppendLine("  }");
        script.AppendLine("  $out += \"$esc]133;A$bel\"");
        script.AppendLine("  if ($global:__guiCursor) {");
        script.AppendLine("    $code = if ($global:__guiCursorSteady) { 6 } else { 5 }");
        script.AppendLine("    $out += \"$esc[$code q\"");
        script.AppendLine("  }");
        script.AppendLine("  $original = ''");
        script.AppendLine("  if ($null -ne $global:__guiPrompt) { $original = & $global:__guiPrompt }");
        script.AppendLine("  if (-not $original) { $original = \"PS $loc> \" }");
        script.AppendLine("  $out += [string]$original");
        script.AppendLine("  $out += \"$esc]133;B$bel\"");
        script.AppendLine("  return $out");
        script.AppendLine("}");
        return script.ToString();
    }
}

/// <summary>
/// Incremental OSC reader. The shell integration writes
/// <c>ESC ] 133 ; A|B|C|D[;exit] BEL</c> around prompts and commands; the parser keeps
/// enough state to survive a sequence split across two PTY reads and reports when a
/// command starts and finishes. It also picks up <c>ESC ] 9 ; 4 ; state ; value ST</c>
/// progress reports on the same pass. Other OSC payloads are skipped.
/// </summary>
internal sealed class ShellMarkParser
{
    private enum State
    {
        Ground,
        Escape,
        Osc,
        OscEscape,
    }

    private const int MaxPayload = 64;

    private State _state;
    private readonly StringBuilder _payload = new(MaxPayload);
    private bool _collect;
    private bool _started;
    private long _startedTicks;

    /// <summary>True between an OSC 133 C and the matching D.</summary>
    public bool CommandRunning { get; private set; }

    /// <summary>
    /// True once an OSC 133 A mark said the shell is showing its prompt and no command has
    /// started since. It is what a "clear screen" needs: whether the cursor belongs to an
    /// idle prompt (which the shell can repaint) or to a running program.
    /// </summary>
    public bool AtPrompt { get; private set; }

    /// <summary>Raised on the UI thread when a command closed with OSC 133 D.</summary>
    public Action<int, TimeSpan>? CommandFinished;

    /// <summary>
    /// Last OSC 9;4 progress: 0 none, 1 normal, 2 error, 3 indeterminate, 4 paused.
    /// </summary>
    public int ProgressState { get; private set; }

    /// <summary>OSC 9;4 percentage, 0-100.</summary>
    public int ProgressValue { get; private set; }

    /// <summary>Raised when an OSC 9;4 report changes the state or the percentage.</summary>
    public Action<int, int>? ProgressChanged;

    /// <summary>Raised on OSC 133 A: a shell prompt is back, so the command is over.</summary>
    public event Action? PromptStarted;

    public void Reset()
    {
        _state = State.Ground;
        _payload.Clear();
        _collect = false;
        _started = false;
        CommandRunning = false;
        AtPrompt = false;
        ProgressState = 0;
        ProgressValue = 0;
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            switch (_state)
            {
                case State.Ground:
                    if (value == 0x1B) _state = State.Escape;
                    break;

                case State.Escape:
                    if (value == (byte)']')
                    {
                        _state = State.Osc;
                        _payload.Clear();
                        _collect = true;
                    }
                    else
                    {
                        // Every other escape sequence is irrelevant here.
                        _state = State.Ground;
                    }
                    break;

                case State.Osc:
                    if (value == 0x07)
                    {
                        Complete();
                    }
                    else if (value == 0x1B)
                    {
                        _state = State.OscEscape;
                    }
                    else if (_collect)
                    {
                        if (_payload.Length < MaxPayload) _payload.Append((char)value);
                        else if (!IsReport()) _collect = false;
                    }
                    break;

                case State.OscEscape:
                    if (value == (byte)'\\')
                    {
                        Complete();
                    }
                    else
                    {
                        // Not a string terminator; resume the payload.
                        _state = State.Osc;
                    }
                    break;
            }
        }
    }

    /// <summary>OSC 133 marks and OSC 9;4 progress are the only payloads kept.</summary>
    private bool IsReport()
    {
        if (_payload.Length < 4) return false;
        if (_payload[0] == '1' && _payload[1] == '3' && _payload[2] == '3' && _payload[3] == ';')
            return true;
        return _payload[0] == '9' && _payload[1] == ';' && _payload[2] == '4' && _payload[3] == ';';
    }

    private void Complete()
    {
        if (IsReport())
        {
            // "9;4;" means progress; everything else that reaches here is an OSC 133 mark.
            if (_payload[0] == '9') ParseProgress();
            else Parse();
        }
        _payload.Clear();
        _collect = false;
        _state = State.Ground;
    }

    /// <summary>
    /// <c>9;4;state[;value]</c>: state 0 clears, 1 normal, 2 error, 3 indeterminate,
    /// 4 paused. A missing or out-of-range value clamps instead of dropping the report.
    /// </summary>
    private void ParseProgress()
    {
        string body = _payload.ToString(4, _payload.Length - 4);
        int separator = body.IndexOf(';');
        int state;
        int value = 0;
        if (separator < 0)
        {
            if (!int.TryParse(body, out state)) return;
        }
        else
        {
            if (!int.TryParse(body[..separator], out state)) return;
            int.TryParse(body[(separator + 1)..], out value);
        }

        if (state < 0 || state > 4) return;
        value = Math.Clamp(value, 0, 100);
        if (state == ProgressState && value == ProgressValue) return;

        ProgressState = state;
        ProgressValue = value;
        ProgressChanged?.Invoke(state, value);
    }

    private void Parse()
    {
        string body = _payload.ToString(4, _payload.Length - 4);
        if (body.Length == 0) return;

        switch (body[0])
        {
            case 'A':
                // A new prompt without a D means the previous command's end mark was lost.
                CommandRunning = false;
                AtPrompt = true;
                _started = false;
                ClearProgress();
                PromptStarted?.Invoke();
                break;

            case 'C':
                if (!_started)
                {
                    _started = true;
                    _startedTicks = Environment.TickCount64;
                }
                CommandRunning = true;
                AtPrompt = false;
                break;

            case 'D':
                if (!_started) return;
                _started = false;
                CommandRunning = false;
                int exit = 0;
                int separator = body.IndexOf(';');
                if (separator >= 0)
                    int.TryParse(body.AsSpan(separator + 1), out exit);
                long elapsed = Environment.TickCount64 - _startedTicks;
                CommandFinished?.Invoke(exit, TimeSpan.FromMilliseconds(Math.Max(0, elapsed)));
                // The command owned the progress bar; its prompt is back.
                ClearProgress();
                break;
        }
    }

    /// <summary>Drops a bar a finished command left behind (the program did not do it itself).</summary>
    private void ClearProgress()
    {
        if (ProgressState == 0 && ProgressValue == 0) return;
        ProgressState = 0;
        ProgressValue = 0;
        ProgressChanged?.Invoke(0, 0);
    }
}
