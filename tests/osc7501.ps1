<#
.SYNOPSIS
    Types OSC 7501 program-status reports into real shells and checks the badge and notifications.

.DESCRIPTION
    The parser itself is covered by tests/Gui.LogicTests (ProgramStatusTests); this script
    covers the part only a window shows: that a report typed into a shell turns into the badge
    in the pane header, that clearing it takes the badge away again, and that blocked/done/error
    raise a desktop notification while the pane is in the background.

    Gui starts the way a person starts it: no -e, one window with the configured shell and its
    normal prompt. "--window-save-state=never" makes this process open a fresh window instead
    of restoring the saved session, and "--no-session-save" keeps the scratch window out of
    that session. Each scenario opens a tab over IPC that runs a generated PowerShell script,
    the same way "+new-tab --command" does, and the script reports over OSC 7501 like a real
    program: no OS foreground and no typing into a live shell are needed. The first tab's
    session comes from the [pty.start] line in the log. Notification scenarios start their
    script waiting on a flag file, switch back to the first tab over IPC, then create the
    flag: the report lands while the tab is in the background, which is the state the
    notification gate checks.

    The badge is read through UI Automation (a WinUI TextBlock exposes its text as the
    element's Name). Toast popups are not visible to UI Automation on Windows 10, so
    notifications are checked through the [notify] line the handler writes to gui-startup.log
    under GUI_LOG=debug. Only a failing scenario saves a screenshot.

    The IPC pipe is per user and Windows session, so the suite refuses to run while another
    Gui instance is open.

    The Gui window is left open when the suite ends so the badges and toasts can be looked at
    afterwards; the scratch directory stays with it because the running app still writes its
    log there. Pass -CloseWindow to close the window and clean the scratch directory instead.
    Either way the next run needs that window gone: the pipe name has no instance id.

    Scenarios:
      blocked         state=blocked + kind=question + msg  -> badge shows "Blocked"
      working         state=working + progress=42          -> badge shows "Working 42%"
      cleared         report, then state=idle              -> badge disappears
      notify-blocked  blocked on a background tab          -> notification fired
      notify-done     done on a background tab             -> notification fired
      notify-fail     error on a background tab            -> notification fired

    Exit code 0 when every scenario passes, 1 otherwise.

.EXAMPLE
    ./tests/osc7501.ps1

.EXAMPLE
    ./tests/osc7501.ps1 -Exe 'D:\Programs\Gui\Gui.exe' -KeepArtifacts

.EXAMPLE
    ./tests/osc7501.ps1 -CloseWindow
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [int]$TimeoutSeconds = 25,
    [switch]$KeepArtifacts,
    [switch]$CloseWindow
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($Exe)) {
    $Exe = Join-Path $repo 'Gui/Gui/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/Gui.exe'
}
if (-not (Test-Path $Exe)) {
    throw "Gui.exe not found at $Exe; build it or pass -Exe"
}

# The pipe name carries no instance id: another Gui would answer the new-tab requests.
$exeName = [IO.Path]::GetFileNameWithoutExtension($Exe)
$others = @(Get-Process -Name $exeName -ErrorAction SilentlyContinue)
if ($others.Count -gt 0) {
    throw "another $exeName is running (PID $($others.Id -join ', ')); close it so new-tab reaches the test window"
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
# Add-Type is session-wide: a second invocation in the same PowerShell session would fail
# with "the type name 'OscWindow' already exists".
if (-not ('OscWindow' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OscWindow {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    public static IntPtr[] Children(IntPtr parent) {
        var list = new System.Collections.Generic.List<IntPtr>();
        EnumChildWindows(parent, (h, l) => { list.Add(h); return true; }, IntPtr.Zero);
        return list.ToArray();
    }
}
"@
}

$work = Join-Path $env:TEMP ('gui-osc7501-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$tempDir = Join-Path $work 'temp'
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

$message = 'Confirm the command?'
$encodedMessage = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($message))
$doneMessage = 'Build finished'
$encodedDone = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($doneMessage))
$failMessage = 'Build broke'
$encodedFail = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($failMessage))

# ---------------- generated scripts ----------------

# Writes a script that reports over OSC 7501 and then holds so its tab, and the badge in its
# header, stay up. Waiting on a flag file lets the suite choose when the report lands, which
# is how a notification scenario gets its report while the tab is in the background. "$e" is
# expanded by PowerShell when the line runs; "\" after it is the protocol's string terminator.
function New-ReportScript {
    param([string]$Name, [string]$Payload, [string]$WaitFlag = '', [string]$ClearFlag = '')

    $lines = @('$e = [char]27')
    if ($WaitFlag) {
        $lines += "while (-not (Test-Path -LiteralPath '$WaitFlag')) { Start-Sleep -Milliseconds 100 }"
    }
    $lines += '[Console]::Out.Write("$e]7501;' + $Payload + '$e\")'
    $lines += '[Console]::Out.Flush()'
    if ($ClearFlag) {
        $lines += "while (-not (Test-Path -LiteralPath '$ClearFlag')) { Start-Sleep -Milliseconds 100 }"
        $lines += '[Console]::Out.Write("$e]7501;state=idle$e\")'
        $lines += '[Console]::Out.Flush()'
    }
    $lines += 'Start-Sleep -Seconds 260'

    $path = Join-Path $work ($Name + '.ps1')
    # A BOM keeps Windows PowerShell from reading a path with non-ASCII characters as ANSI.
    [IO.File]::WriteAllText($path, ($lines -join "`r`n") + "`r`n", (New-Object System.Text.UTF8Encoding $true))
    return $path
}

# ---------------- ipc ----------------

# Same wire shape as Gui.Cli (IpcProtocol): one JSON line each way over the per-user pipe.
# "new-tab" can carry a command, so a scenario's script runs in its own tab; "focus" switches
# tabs back without an OS foreground.
function Send-Ipc {
    param([hashtable]$Request)

    $pipeName = 'gui-terminal-Gui-' + $env:USERNAME.ToLowerInvariant() + '-' + (Get-Process -Id $PID).SessionId
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(3000)
        $bytes = [Text.Encoding]::UTF8.GetBytes(($Request | ConvertTo-Json -Compress) + "`n")
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
        $reader = New-Object System.IO.StreamReader($pipe, [Text.Encoding]::UTF8)
        $answer = $reader.ReadLine()
        if ($answer -and $answer -notmatch '"ok"\s*:\s*true') { throw "ipc rejected: $answer" }
    } finally {
        $pipe.Dispose()
    }
}

function New-ScriptTab {
    param([string]$Name, [string]$Payload, [string]$WaitFlag = '', [string]$ClearFlag = '')

    $script = New-ReportScript -Name $Name -Payload $Payload -WaitFlag $WaitFlag -ClearFlag $ClearFlag
    Send-Ipc @{ action = 'new-tab'; title = $Name;
        command = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $script + '"' }
}

# ---------------- ui automation ----------------

# The XAML content is not part of the top-level window's tree: WinUI hosts it in a child
# window (InputSiteWindowClass), which is where the TextBlocks live. Pick the child that
# exposes the most elements, falling back to the window itself.
function Get-ContentWindow {
    param([IntPtr]$Handle)

    $best = $Handle
    $bestCount = 0
    foreach ($child in [OscWindow]::Children($Handle)) {
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($child)
            if (-not $root) { continue }
            $count = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition).Count
            if ($count -gt $bestCount) { $bestCount = $count; $best = $child }
        } catch {
            # A child that cannot be inspected is simply not the content window.
        }
    }
    return $best
}

function Get-TextNames {
    param([IntPtr]$Handle)

    # Resolved per call: the XAML window is created a moment after the top-level one, and a
    # handle cached too early would answer "no text" for the rest of the run.
    $root = [System.Windows.Automation.AutomationElement]::FromHandle((Get-ContentWindow $Handle))
    if (-not $root) { return @() }
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $names = New-Object System.Collections.Generic.List[string]
    foreach ($element in $all) {
        try {
            $current = $element.Current
            if ($current.ControlType -eq [System.Windows.Automation.ControlType]::Text -and $current.Name) {
                $names.Add($current.Name)
            }
        } catch {
            # An element that went away between the walk and the read is simply not there.
        }
    }
    return $names
}

function Wait-Badge {
    param([IntPtr]$Handle, [string[]]$Text, [double]$Seconds, [switch]$Gone)

    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $names = @(Get-TextNames $Handle)
        # The badge label follows the app language, so a scenario accepts every spelling.
        # Compare case-sensitively: tab titles are the lowercase scenario names, and
        # "-contains" would let the "blocked" title pass for the "Blocked" badge.
        $present = @($Text | Where-Object { $names -ccontains $_ }).Count -gt 0
        if ($Gone) {
            if (-not $present) { return $names }
        } elseif ($present) {
            return $names
        }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Save-Screenshot {
    param([IntPtr]$Handle, [string]$Path)

    try {
        Add-Type -AssemblyName System.Drawing
        $rect = New-Object OscWindow+RECT
        [OscWindow]::GetClientRect($Handle, [ref]$rect) | Out-Null
        $point = New-Object OscWindow+POINT
        [OscWindow]::ClientToScreen($Handle, [ref]$point) | Out-Null
        $width = $rect.R - $rect.L
        $height = $rect.B - $rect.T
        $bitmap = New-Object System.Drawing.Bitmap $width, $height
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.CopyFromScreen($point.X, $point.Y, 0, 0, (New-Object System.Drawing.Size $width, $height))
        $graphics.Dispose()
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
    } catch {
        Write-Warning "screenshot failed: $($_.Exception.Message)"
    }
}

# ---------------- log ----------------

function Get-LogLineCount {
    param([string]$Path)

    if (-not (Test-Path $Path)) { return 0 }
    return @(Get-Content $Path).Count
}

# Each shell start is one [pty.start] line naming its session. The suite reads the first
# tab's session there instead of assuming an id, for the "focus" request that puts the first
# tab back in front of a notification scenario's tab. Returns $null when none appeared.
function Wait-PaneStart {
    param([string]$Path, [int]$Skip, [double]$Seconds)

    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $lines = @(Get-Content $Path -ErrorAction SilentlyContinue)
        if ($lines.Count -gt $Skip) {
            $starts = @($lines[$Skip..($lines.Count - 1)] | Where-Object { $_ -match '\[pty\.start\]' })
            if ($starts.Count -gt 0) {
                if ($starts[-1] -match 'session=([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})') { return $Matches[1] }
                return $null
            }
        }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

# Waits for a [notify] line carrying the state, ignoring everything up to Skip so a previous
# scenario's line does not count.
function Wait-NotifyLine {
    param([string]$Path, [int]$Skip, [string]$State, [double]$Seconds)

    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        if (Test-Path $Path) {
            $lines = @(Get-Content $Path)
            if ($lines.Count -gt $Skip) {
                $notify = @($lines[$Skip..($lines.Count - 1)] |
                    Where-Object { $_ -match '\[notify\]' -and $_ -match "state=$State" })
                if ($notify.Count -gt 0) { return $true }
            }
        }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

# ---------------- the one window ----------------

# The badge shows the state's short name, which follows the app language.
$badgeBlocked = @('Blocked', '等待操作')
$badgeWorking42 = @('Working 42%', '运行中 42%')
$badgeDone = @('Done', '已完成')
$badgeError = @('Error', '失败')

$scenarios = @(
    [pscustomobject]@{
        Name    = 'blocked'
        Payload = "state=blocked:kind=question:app=demo:msg=$encodedMessage"
        Expect  = $badgeBlocked
        Clears  = $false
        Notify  = $null
    },
    [pscustomobject]@{
        Name    = 'working'
        Payload = 'state=working:progress=42'
        Expect  = $badgeWorking42
        Clears  = $false
        Notify  = $null
    },
    [pscustomobject]@{
        Name    = 'cleared'
        Payload = "state=blocked:kind=question:app=demo:msg=$encodedMessage"
        Expect  = $badgeBlocked
        Clears  = $true
        Notify  = $null
    },
    [pscustomobject]@{
        Name    = 'notify-blocked'
        Payload = "state=blocked:kind=question:app=demo:msg=$encodedMessage"
        Expect  = $badgeBlocked
        Clears  = $false
        Notify  = 'Blocked'
    },
    [pscustomobject]@{
        Name    = 'notify-done'
        Payload = "state=done:app=demo:msg=$encodedDone"
        Expect  = $badgeDone
        Clears  = $false
        Notify  = 'Done'
    },
    [pscustomobject]@{
        Name    = 'notify-fail'
        Payload = "state=error:app=demo:msg=$encodedFail"
        Expect  = $badgeError
        Clears  = $false
        Notify  = 'Error'
    }
)

$results = @()
$log = Join-Path $tempDir 'gui-startup.log'

$saved = @{ TEMP = $env:TEMP; TMP = $env:TMP; GUI_LOG = $env:GUI_LOG }
$env:TEMP = $tempDir
$env:TMP = $tempDir
$env:GUI_LOG = 'debug'
try {
    # A launch override makes the app open a fresh window instead of restoring the saved one;
    # --no-session-save keeps this scratch window out of the user's saved session.
    $process = Start-Process -FilePath $Exe -PassThru -ArgumentList @(
        '--window-save-state=never',
        '--no-session-save')
} finally {
    $env:TEMP = $saved.TEMP
    $env:TMP = $saved.TMP
    $env:GUI_LOG = $saved.GUI_LOG
}

$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
do {
    Start-Sleep -Milliseconds 150
    $process.Refresh()
} until ($process.MainWindowHandle -ne 0 -or $process.HasExited -or [DateTime]::UtcNow -gt $deadline)

$handle = $process.MainWindowHandle
if ($handle -eq 0) {
    Write-Host 'FAIL: no window appeared'
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $null = $process.WaitForExit(5000)
    if (-not $KeepArtifacts) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
}
# Raise the window once so the background-tab check sees it in front, but leave it a normal
# window: it outlives the run, and a topmost test window blocks every other app until closed.
[OscWindow]::ShowWindow($handle, 9) | Out-Null
[OscWindow]::SetForegroundWindow($handle) | Out-Null

try {
    # The first tab is the shell the window opened with: it stays at a prompt and is the tab
    # the notification scenarios switch back to.
    $firstSession = Wait-PaneStart -Path $log -Skip 0 -Seconds $TimeoutSeconds
    if (-not $firstSession) {
        throw 'no shell started in the first tab; rebuild the app before running the suite'
    }

    foreach ($scenario in $scenarios) {
        $flag = Join-Path $work ($scenario.Name + '.flag')
        if (Test-Path -LiteralPath $flag) { Remove-Item -LiteralPath $flag -Force }

        $status = 'FAIL'
        $detail = ''
        try {
            if ($scenario.Notify) {
                # The script starts paused on the flag; only after the first tab is back in
                # front does the flag let the report land, with this tab in the background
                # (the state the notification gate checks).
                New-ScriptTab -Name $scenario.Name -Payload $scenario.Payload -WaitFlag $flag
                Send-Ipc @{ action = 'focus'; session = $firstSession }
                $logSkip = Get-LogLineCount $log
                New-Item -ItemType File -Path $flag -Force | Out-Null
                if (Wait-NotifyLine -Path $log -Skip $logSkip -State $scenario.Notify -Seconds $TimeoutSeconds) {
                    $status = 'ok'
                } else {
                    $detail = "no [notify] state=$($scenario.Notify) line for the background tab"
                }
            } else {
                $clear = ''
                if ($scenario.Clears) { $clear = $flag }
                New-ScriptTab -Name $scenario.Name -Payload $scenario.Payload -ClearFlag $clear
                $names = Wait-Badge -Handle $handle -Text $scenario.Expect -Seconds $TimeoutSeconds
                if (-not $names) {
                    $detail = "badge never showed '$($scenario.Expect)'"
                    $seen = @(Get-TextNames $handle) -join ' | '
                    if ($seen) { $detail += "; text seen: $seen" }
                } elseif ($scenario.Clears) {
                    New-Item -ItemType File -Path $flag -Force | Out-Null
                    if (Wait-Badge -Handle $handle -Text $scenario.Expect -Seconds $TimeoutSeconds -Gone) {
                        $status = 'ok'
                    } else {
                        $detail = 'badge stayed after the report was cleared'
                        $seen = @(Get-TextNames $handle) -join ' | '
                        if ($seen) { $detail += "; text seen: $seen" }
                    }
                } else {
                    $status = 'ok'
                }
            }
        } catch {
            $detail = $_.Exception.Message
        }

        if ($status -ne 'ok') {
            $shot = Join-Path $work ($scenario.Name + '.png')
            Save-Screenshot -Handle $handle -Path $shot
            if (Test-Path $shot) { $detail += " (screenshot: $shot)" }
        }

        $results += [pscustomobject]@{ scenario = $scenario.Name; result = $status; detail = $detail }
        Write-Host ("{0,-16} {1,-4} {2}" -f $scenario.Name, $status, $detail)
    }
} finally {
    $env:TEMP = $saved.TEMP
    $env:TMP = $saved.TMP
    $env:GUI_LOG = $saved.GUI_LOG

    if ($CloseWindow) {
        $process.Refresh()
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        # A second run in the same PowerShell session must not see this window still exiting.
        $null = $process.WaitForExit(5000)
    } elseif (-not $process.HasExited) {
        Write-Host ''
        Write-Host "Gui window left open for inspection (PID $($process.Id)); close it before the next run."
        Write-Host "scratch: $work"
    }
}

Write-Host ''
$failed = @($results | Where-Object { $_.result -ne 'ok' }).Count

# The scratch TEMP holds the log this run produced; ERROR lines there mean the feature broke
# the app, not just the badge. Match the level column only: a [notify] line mentioning
# state=Error is a passing notification, not a failure.
if (Test-Path $log) {
    $errors = @(Get-Content $log | Where-Object { $_ -match '^\S+ \S+ ERROR\b' })
    if ($errors.Count -gt 0) {
        Write-Host 'log errors:'
        $errors | Select-Object -First 8 | ForEach-Object { Write-Host ('  ' + $_) }
        $failed++
    }
}

if ($failed -eq 0) {
    Write-Host 'PASS: the OSC 7501 badge follows the report and background tabs notify'
    # The scratch directory is the running window's TEMP; removing it would break its log.
    if ($CloseWindow -and -not $KeepArtifacts) {
        Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    exit 0
}

Write-Host "FAIL: $failed problem(s); artifacts in $work"
exit 1
