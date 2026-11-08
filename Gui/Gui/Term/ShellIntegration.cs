using System;
using System.Text;

namespace Gui.Term;

/// <summary>
/// PowerShell shell integration. Ghostty upstream ships bash/zsh/fish/elvish/nushell
/// scripts only, so the Windows port supplies its own: a prompt wrapper that emits the
/// OSC 133 semantic marks (prompt start/end, command start/finish) plus the OSC 7
/// working-directory report. <see cref="ShellMarkParser"/> reads those marks back from
/// the PTY stream so the app knows when a command is still running.
/// </summary>
internal static class ShellIntegration
{
    /// <summary>
    /// Wraps a bare PowerShell command line with the integration script. The command is
    /// returned unchanged when integration is off or when it names another shell.
    /// </summary>
    public static string Inject(string shell, string mode, ShellIntegrationFeatures features)
    {
        if (mode is not ("detect" or "powershell" or "pwsh")) return shell;
        return shell + " -NoExit -EncodedCommand " + Encode(Script(features));
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
/// Incremental OSC 133 reader. The shell integration writes
/// <c>ESC ] 133 ; A|B|C|D[;exit] BEL</c> around prompts and commands; the parser keeps
/// enough state to survive a sequence split across two PTY reads and reports when a
/// command starts and finishes. Other OSC payloads are skipped.
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

    /// <summary>Raised on the UI thread when a command closed with OSC 133 D.</summary>
    public Action<int, TimeSpan>? CommandFinished;

    public void Reset()
    {
        _state = State.Ground;
        _payload.Clear();
        _collect = false;
        _started = false;
        CommandRunning = false;
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
                        else if (!IsTarget()) _collect = false;
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

    private bool IsTarget() =>
        _payload.Length >= 4 &&
        _payload[0] == '1' && _payload[1] == '3' && _payload[2] == '3' && _payload[3] == ';';

    private void Complete()
    {
        if (IsTarget()) Parse();
        _payload.Clear();
        _collect = false;
        _state = State.Ground;
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
                _started = false;
                break;

            case 'C':
                if (!_started)
                {
                    _started = true;
                    _startedTicks = Environment.TickCount64;
                }
                CommandRunning = true;
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
                break;
        }
    }
}
