<#
.SYNOPSIS
    Compares Gui's start-up, idle cost, flood throughput and the cost of a full layout with
    terminals installed on the same machine (Windows Terminal through wt.exe, otty through
    otty.exe).

.DESCRIPTION
    Every terminal is driven the way a user drives it: an empty window with a prompt, then a
    fixed amount of output written by tests/perf-emitter through the pane. Each mode reports
    its own numbers:

    startup  process start -> a visible window of the terminal's own process, then idle CPU
             and private bytes over -IdleSeconds with a prompt on screen.
    emit     a 64 MiB (or -MB) flood through the pane: the emitter's own wall time (the
             pseudoconsole only accepts bytes as fast as the terminal drains them), the
             terminal's CPU while it absorbs and paints, the time until its CPU goes flat
             again, and the peak private bytes during the flood.

    tabs     ten (or -Tabs) tabs in one window, each with its own shell: private bytes with
             the layout settled, the idle CPU the extra panes cost, and the difference to the
             empty window divided by the tabs added.
    split    the same, for one tab split into -Splits + 1 panes.

    Every window in these two passes starts with -Command (default "cmd /k") and every tab or
    pane they add runs the same thing, so the layout numbers are not mixed with a second
    shell: Windows Terminal and otty are told the shell on the command line of each new tab or
    pane, Gui through a one-key config file it is launched with (its chords only run the
    configured command). -DefaultShell therefore covers startup and emit only. otty reopens
    its saved session, so its first panes keep that session's shell; they are part of the
    baseline, and its saved session is put back the way it was.

    The tabs and split passes build the layout the way a user does: Gui and Windows Terminal
    through their own chords (Ctrl+Shift+T for a tab; Ctrl+Shift+O and Alt+Shift+plus for a
    split), otty through its control CLI (otty tab new / otty pane split). Every new tab or
    pane starts a shell, so the shell count read out of the process tree says whether the
    layout is complete -- a dropped chord or a refused IPC call shows up there instead of
    hiding in a smaller footprint. baseMb is the same window measured before the layout and
    shells/baseShells is the count it had, so terminals with different base footprints (otty
    reopens its saved session) stay comparable, and addedMb is that difference per extra tab
    or pane. tests/perf-gui.ps1 measures the same two layouts for Gui alone, against a saved
    baseline and with pass/fail limits.

    Read the emit numbers with the buffering difference in mind: Gui's reader thread takes the
    pipe and queues the backlog for the UI thread, so its MB/s is pipe acceptance and the queue
    shows up as a private-byte spike. Windows Terminal parses on its UI thread, so its MB/s is
    real processing speed and its memory stays flat. The end-to-end column (emit + drain) is
    the one that compares.

    Windows are measured in the foreground: a background window is composited, and in a
    terminal that parses on the UI thread also processed, at a throttled rate.

.EXAMPLE
    ./tests/perf-terminals.ps1 -Mode startup -Runs 3
    ./tests/perf-terminals.ps1 -Mode emit -MB 64 -Runs 3
    ./tests/perf-terminals.ps1 -Mode tabs -Tabs 10 -Runs 3
    ./tests/perf-terminals.ps1 -Mode split -Splits 4 -Runs 3
    ./tests/perf-terminals.ps1 -Mode startup -Terminal gui,wt -DefaultShell
#>
[CmdletBinding()]
param(
    [ValidateSet('gui', 'wt', 'otty')][string[]]$Terminal = @('gui', 'wt', 'otty'),
    [ValidateSet('startup', 'emit', 'tabs', 'split')][string]$Mode = 'startup',
    [int]$Runs = 3,
    [int]$MB = 64,
    [int]$Tabs = 10,
    [int]$Splits = 4,
    [string]$Command = 'cmd /k',
    [int]$IdleSeconds = 10,
    [switch]$DefaultShell,
    [switch]$OttyPaste,
    [string]$GuiExe = '',
    [string]$WtExe = '',
    [string]$OttyExe = ''
)

$ErrorActionPreference = 'Stop'

if ($DefaultShell -and $Mode -in @('tabs', 'split')) {
    Write-Warning "-DefaultShell covers startup and emit only; the $Mode pass runs -Command ($Command)"
}

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class Win {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static List<IntPtr> Visible() {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            RECT r; if (!GetWindowRect(h, out r)) return true;
            if (r.R - r.L < 300 || r.B - r.T < 200) return true;
            if (GetWindowTextLength(h) == 0) return true;
            list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
    public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
    public static string Title(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
    public static void Focus(IntPtr h) {
        ShowWindow(h, 9);
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint mine = GetCurrentThreadId();
        AttachThreadInput(mine, fgThread, true);
        SetForegroundWindow(h);
        AttachThreadInput(mine, fgThread, false);
        SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0003);
    }
    public static void PasteShift() {
        keybd_event(0x11, 0x1D, 0, UIntPtr.Zero);
        keybd_event(0x10, 0x2A, 0, UIntPtr.Zero);
        keybd_event(0x56, 0x2F, 0, UIntPtr.Zero);
        keybd_event(0x56, 0x2F, 2, UIntPtr.Zero);
        keybd_event(0x10, 0x2A, 2, UIntPtr.Zero);
        keybd_event(0x11, 0x1D, 2, UIntPtr.Zero);
    }
    public static void PasteCtrl() {
        keybd_event(0x11, 0x1D, 0, UIntPtr.Zero);
        keybd_event(0x56, 0x2F, 0, UIntPtr.Zero);
        keybd_event(0x56, 0x2F, 2, UIntPtr.Zero);
        keybd_event(0x11, 0x1D, 2, UIntPtr.Zero);
    }
    public static void Enter() {
        keybd_event(0x0D, 0x1C, 0, UIntPtr.Zero);
        keybd_event(0x0D, 0x1C, 2, UIntPtr.Zero);
    }
    // "vk, scancode" pairs, pressed in order and released in reverse so the modifiers are
    // still down when the terminal sees the key.
    public static void Combo(byte[] keys) {
        for (int i = 0; i < keys.Length; i += 2) keybd_event(keys[i], keys[i + 1], 0, UIntPtr.Zero);
        for (int i = keys.Length - 2; i >= 0; i -= 2) keybd_event(keys[i], keys[i + 1], 2, UIntPtr.Zero);
    }
}
"@

# ---------------- paths ----------------

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($GuiExe)) {
    $GuiExe = Join-Path $repoRoot 'Gui/Gui/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/Gui.exe'
}
if ([string]::IsNullOrEmpty($WtExe)) {
    $WtExe = (Get-Command wt.exe -ErrorAction SilentlyContinue).Source
}
if ([string]::IsNullOrEmpty($OttyExe)) {
    $OttyExe = (Get-Command otty.exe -ErrorAction SilentlyContinue).Source
}

$exeFor = @{ gui = $GuiExe; wt = $WtExe; otty = $OttyExe }
$processFor = @{ gui = 'Gui'; wt = 'WindowsTerminal'; otty = 'otty' }

foreach ($app in $Terminal) {
    if (-not $exeFor[$app] -or -not (Test-Path $exeFor[$app])) {
        Write-Warning "$app : executable not found ($($exeFor[$app])); skipped"
    }
}
$Terminal = @($Terminal | Where-Object { $exeFor[$_] -and (Test-Path $exeFor[$_]) })
if ($Terminal.Count -eq 0) { throw 'no terminal to measure' }

$emitterProject = Join-Path $PSScriptRoot 'perf-emitter'
$emitterExe = Join-Path $emitterProject 'bin/Release/net10.0/emit.exe'
if (-not (Test-Path $emitterExe)) {
    Write-Host 'building tests/perf-emitter...'
    & dotnet build $emitterProject -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "emitter build failed ($LASTEXITCODE)" }
}

$workDir = Join-Path $env:TEMP 'gui-perf-emitter'
New-Item -ItemType Directory -Path $workDir -Force | Out-Null

# Run a copy from %TEMP%: every terminal is handed the same short, space-free path, and otty's
# git-bash pane gets a C: path even when the repository lives on another drive.
$runner = Join-Path $workDir 'emit.exe'
Copy-Item (Join-Path (Split-Path -Parent $emitterExe) '*') $workDir -Force

# Gui takes a pane's shell from the "command" config key, not from a launch flag ("-e" only
# covers the first tab), so the layout passes hand it a config file holding that one key: the
# window and every tab opened in it then run the same -Command. The file is read after the
# default config, so it wins over a "command" the user set there.
$guiLayoutConfig = Join-Path $workDir 'gui-layout-config'
Set-Content -Path $guiLayoutConfig -Value ('command = ' + $Command) -Encoding utf8

# A window that existed before the run belongs to someone else: never close or count it.
$protected = @{}
foreach ($name in 'Gui', 'WindowsTerminal', 'otty') {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object { $protected[$_.Id] = $true }
}

# ---------------- helpers ----------------

function Wait-Window($before, [string]$processName, [int]$timeoutSeconds) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $timeoutSeconds) {
        foreach ($h in [Win]::Visible()) {
            if ($before -contains $h) { continue }
            $owner = Get-Process -Id ([int][Win]::Pid($h)) -ErrorAction SilentlyContinue
            if (-not $owner -or $owner.ProcessName -ne $processName) { continue }
            $seen = [math]::Round($watch.Elapsed.TotalSeconds, 2)
            Start-Sleep -Milliseconds 400
            if (-not [Win]::IsWindow($h)) { continue }
            $owner.Refresh()
            if ($owner.HasExited) { continue }
            return @{ Handle = $h; Pid = [int]$owner.Id; Seconds = $seen; Title = [Win]::Title($h) }
        }
        Start-Sleep -Milliseconds 20
    }
    return $null
}

function Close-Terminal([IntPtr]$handle, [int]$owner) {
    [Win]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 1500
    if ($protected.ContainsKey($owner)) { return }
    $p = Get-Process -Id $owner -ErrorAction SilentlyContinue
    if ($p) { Stop-Process -Id $owner -Force -ErrorAction SilentlyContinue }
}

# Gui's chords, in "vk, scancode" pairs: Ctrl+Shift+T for a tab, Ctrl+Shift+O for a split.
# Windows Terminal and otty are driven by their own command lines instead, which is also where
# they take the shell to run, so their tabs cannot end up on a default profile.
$Chords = @{
    tabs  = [byte[]](0x11, 0x1D, 0x10, 0x2A, 0x54, 0x14)
    split = [byte[]](0x11, 0x1D, 0x10, 0x2A, 0x4F, 0x18)
}

# Waits until the process has been quiet for two consecutive samples, so start-up work and
# the paint that follows a chord are never mistaken for idle cost.
function Wait-Quiet([Diagnostics.Process]$process, [double]$quotaSeconds = 0.005,
    [int]$quietTicks = 2, [double]$capSeconds = 20) {
    $settle = [Diagnostics.Stopwatch]::StartNew()
    $quiet = 0
    $process.Refresh()
    $previous = $process.TotalProcessorTime
    while ($settle.Elapsed.TotalSeconds -lt $capSeconds -and $quiet -lt $quietTicks) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) { return }
        $used = ($process.TotalProcessorTime - $previous).TotalSeconds
        $previous = $process.TotalProcessorTime
        if ($used -lt $quotaSeconds) { $quiet++ } else { $quiet = 0 }
    }
}

function Set-Layout([string]$app, [string]$mode, [IntPtr]$window, [int]$count) {
    if ($count -le 0) { return }
    [Win]::Focus($window)
    Start-Sleep -Milliseconds 500

    # otty builds the layout over its control socket and takes the shell as one executable
    # (not a command line), so only the first word of -Command is passed. Each new tab or pane
    # takes the focus, which is what nests the splits.
    if ($app -eq 'otty') {
        $exe = ($Command -split '\s+')[0]
        for ($i = 0; $i -lt $count; $i++) {
            if ($mode -eq 'tabs') {
                & $OttyExe tab new --command $exe | Out-Null
            }
            else {
                & $OttyExe pane split --direction right --command $exe | Out-Null
            }
            Start-Sleep -Milliseconds 400
        }
        return
    }

    # Windows Terminal's new tab would follow its default profile, so the tab (or split) is
    # asked for on the command line with the shell named: "-w 0" is the window just measured.
    if ($app -eq 'wt') {
        $wtArgs = @('-w', '0')
        if ($mode -eq 'tabs') { $wtArgs += 'new-tab' } else { $wtArgs += @('split-pane', '-V') }
        $wtArgs += ($Command -split '\s+')
        for ($i = 0; $i -lt $count; $i++) {
            & $WtExe @wtArgs | Out-Null
            # The new tab or pane has to exist and be focused before the next split.
            Start-Sleep -Milliseconds 900
        }
        return
    }

    # Gui's chords open the tab or split on the "command" the config file it was launched with
    # carries, so there is nothing extra to pass here.
    $keys = $Chords[$mode]
    for ($i = 0; $i -lt $count; $i++) {
        [Win]::Combo($keys)
        # The chord has to be handled, and the new tab or pane focused, before the next one;
        # a chord that arrives during the previous one's layout is dropped.
        Start-Sleep -Milliseconds 700
    }
}

# otty's ids for its tabs or panes ("tabs"/"panes"), read over the control socket. An empty
# list means the query failed, which the cleanup treats as "do not touch anything".
function Get-OttyIds([string]$kind) {
    try {
        $json = & $OttyExe $kind list --json 2>&1 | Out-String
        return @((ConvertFrom-Json $json).data | ForEach-Object { $_.id })
    }
    catch {
        return @()
    }
}

# otty reopens its saved session when it starts and writes the layout back when it closes, so
# a measured window would become the session the user reopens next time. The tabs or panes
# this pass added are closed again before the window goes away.
function Clear-OttyLayout([string]$mode, [string[]]$tabIds, [string[]]$paneIds) {
    if ($mode -eq 'tabs') {
        if ($tabIds.Count -eq 0) { return }
        foreach ($id in Get-OttyIds 'tab') {
            if ($tabIds -notcontains $id) { & $OttyExe tab close -y $id | Out-Null }
        }
    }
    else {
        if ($paneIds.Count -eq 0) { return }
        foreach ($id in Get-OttyIds 'pane') {
            if ($paneIds -notcontains $id) { & $OttyExe pane close -y $id | Out-Null }
        }
    }
    Start-Sleep -Milliseconds 500
}

# Shells under the terminal, found by walking the process tree: a shell is never a direct
# child (the pseudoconsole host sits in between), so every descendant whose name is a shell
# counts. This is how the table can tell a chord that opened its pane from one that was lost.
function Measure-Shells([int]$rootPid) {
    try {
        $processes = Get-CimInstance Win32_Process -ErrorAction Stop |
            Select-Object ProcessId, ParentProcessId, Name
    }
    catch {
        return 0
    }

    $children = @{}
    foreach ($p in $processes) {
        $parent = [int]$p.ParentProcessId
        if (-not $children.ContainsKey($parent)) { $children[$parent] = New-Object System.Collections.ArrayList }
        [void]$children[$parent].Add($p)
    }

    $count = 0
    $pending = New-Object System.Collections.Stack
    $pending.Push($rootPid)
    while ($pending.Count -gt 0) {
        $owner = [int]$pending.Pop()
        if (-not $children.ContainsKey($owner)) { continue }
        foreach ($child in $children[$owner]) {
            if ($child.Name -match '^(pwsh|powershell|cmd|bash|wsl|nu)\.exe$') { $count++ }
            $pending.Push([int]$child.ProcessId)
        }
    }
    return $count
}

function New-Flood([string]$app, [int]$run) {
    $result = Join-Path $workDir ("result-{0}-{1}.txt" -f $app, $run)
    $cmd = Join-Path $workDir ("flood-{0}-{1}.cmd" -f $app, $run)
    Remove-Item $result, $cmd -ErrorAction SilentlyContinue
    Set-Content -Path $cmd -Encoding ascii -Value @(
        ('"{0}" {1} "{2}"' -f $runner, $MB, $result),
        # Keep the pane alive after the flood so the drain can be measured and the window closed.
        'timeout /t 300 >nul'
    )
    return @{ Cmd = $cmd; Result = $result }
}

function Get-Launch([string]$app, [string]$mode, [int]$run) {
    # The layout passes start from the same single-pane window as "startup", with the same
    # -Command, and add the tabs or panes on top of it.
    if ($mode -in @('startup', 'tabs', 'split')) {
        $layout = $mode -ne 'startup'
        if ($DefaultShell -and -not $layout) {
            switch ($app) {
                'gui'  { return @{ Exe = $GuiExe;  Args = $null } }
                'wt'   { return @{ Exe = $WtExe;   Args = '-w new' } }
                'otty' { return @{ Exe = $OttyExe; Args = 'open' } }
            }
        }
        switch ($app) {
            # --no-session-save: a measured window must not replace the saved session. The
            # config file is what makes the tabs that follow run -Command as well.
            'gui' {
                if ($layout) {
                    return @{ Exe = $GuiExe
                        Args = ('--no-session-save --config-file="{0}" -e "{1}"' -f $guiLayoutConfig, $Command) }
                }
                return @{ Exe = $GuiExe; Args = ('-e "{0}"' -f $Command) }
            }
            'wt'   { return @{ Exe = $WtExe;   Args = ('-w new {0}' -f $Command) } }
            'otty' { return @{ Exe = $OttyExe; Args = 'open' } }
        }
    }

    if ($app -eq 'otty') {
        # otty restores a git-bash pane and ignores --command while doing so, so the flood is
        # typed into that pane with a bash-style path.
        $result = Join-Path $workDir ("result-otty-{0}.txt" -f $run)
        Remove-Item $result -ErrorAction SilentlyContinue
        $exe = $runner -replace [regex]::Escape([string][char]92), '/'
        $res = $result -replace [regex]::Escape([string][char]92), '/'
        return @{
            Exe = $OttyExe
            Args = 'open'
            Flood = @{ Command = ('"{0}" {1} "{2}"' -f $exe, $MB, $res); Result = $result }
        }
    }

    $flood = New-Flood $app $run
    switch ($app) {
        'gui' { return @{ Exe = $GuiExe; Args = ('-e "cmd /c {0}"' -f $flood.Cmd); Flood = @{ Result = $flood.Result } } }
        'wt'  { return @{ Exe = $WtExe;  Args = ('-w new cmd /c "{0}"' -f $flood.Cmd); Flood = @{ Result = $flood.Result } } }
    }
}

function Wait-Flood($flood, $term, [int]$timeoutSeconds) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $timeoutSeconds) {
        if ($term) {
            $term.Refresh()
            if (-not $term.HasExited -and $term.PrivateMemorySize64 -gt $script:peakBytes) {
                $script:peakBytes = $term.PrivateMemorySize64
            }
        }
        if (Test-Path $flood.Result) {
            $line = (Get-Content $flood.Result -Raw).Trim() -split '\s+'
            if ($line.Count -ge 2) {
                return @{ Ms = [double]$line[0]; Bytes = [double]$line[1] }
            }
        }
        Start-Sleep -Milliseconds 50
    }
    return $null
}

function Start-Flood([string]$app, $launch, [IntPtr]$window, $term) {
    if ($app -ne 'otty') { return $null }

    Start-Sleep -Milliseconds 3000
    Set-Clipboard -Value $launch.Flood.Command
    [Win]::Focus($window)
    Start-Sleep -Milliseconds 400
    [Win]::PasteShift()
    Start-Sleep -Milliseconds 500
    [Win]::Enter()
    $result = Wait-Flood $launch.Flood $term 8
    if ($result) { return $result }

    Set-Clipboard -Value $launch.Flood.Command
    [Win]::Focus($window)
    Start-Sleep -Milliseconds 300
    [Win]::PasteCtrl()
    Start-Sleep -Milliseconds 300
    [Win]::Enter()
    return Wait-Flood $launch.Flood $term 8
}

# ---------------- runs ----------------

$rows = @()
foreach ($app in $Terminal) {
    if ($app -eq 'otty') {
        $running = Get-Process -Name otty -ErrorAction SilentlyContinue
        if ($running) {
            Write-Warning 'otty is already running; close it to measure otty (single instance)'
            continue
        }
        if ($Mode -eq 'emit' -and -not $OttyPaste) {
            $slash = [regex]::Escape([string][char]92)
            $hint = '"' + ($runner -replace $slash, '/') + '" ' + $MB + ' "' +
                ((Join-Path $workDir 'result-otty.txt') -replace $slash, '/') + '"'
            Write-Warning 'otty ignores --command while restoring its session; paste the line below into an otty pane, or pass -OttyPaste with a single otty window'
            Write-Host ('  ' + $hint)
            continue
        }
    }

    for ($i = 1; $i -le $Runs; $i++) {
        $launch = Get-Launch $app $Mode $i
        $before = [Win]::Visible()
        $process = if ($launch.Args) {
            Start-Process -FilePath $launch.Exe -ArgumentList $launch.Args -PassThru
        } else {
            Start-Process -FilePath $launch.Exe -PassThru
        }

        $found = Wait-Window $before $processFor[$app] 30
        if (-not $found) {
            Write-Warning ("{0} run {1}: no window appeared" -f $app, $i)
            if ($process -and -not $process.HasExited -and -not $protected.ContainsKey($process.Id)) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
            continue
        }
        if ($protected.ContainsKey($found.Pid)) {
            Write-Warning ("{0} run {1}: window belongs to a process that was already running" -f $app, $i)
            Close-Terminal $found.Handle $found.Pid
            continue
        }

        $window = $found.Handle
        $term = Get-Process -Id $found.Pid -ErrorAction SilentlyContinue
        if (-not $term) { continue }

        if ($Mode -eq 'startup') {
            [Win]::Focus($window)
            Start-Sleep -Milliseconds 2500
            $term.Refresh()
            $cpu0 = $term.TotalProcessorTime
            Start-Sleep -Seconds $IdleSeconds
            $term.Refresh()
            $cpu1 = $term.TotalProcessorTime
            $rows += [pscustomobject]@{
                terminal = $app
                run      = $i
                window   = $found.Seconds
                idleCpu  = [math]::Round(100.0 * ($cpu1 - $cpu0).TotalSeconds / $IdleSeconds, 2)
                private  = [math]::Round($term.PrivateMemorySize64 / 1MB, 1)
            }
            Write-Host ("{0} {1}: window {2}s idle {3}% private {4} MB" -f $app, $i, $found.Seconds,
                $rows[-1].idleCpu, $rows[-1].private)
            Close-Terminal $window $found.Pid
            Start-Sleep -Milliseconds 800
            continue
        }

        if ($Mode -in @('tabs', 'split')) {
            # otty reopens its saved session, so the window the layout is built in may already
            # hold tabs and panes: they count towards -Tabs and are closed again afterwards.
            $ottyTabs = @()
            $ottyPanes = @()
            if ($app -eq 'otty') {
                $ottyTabs = Get-OttyIds 'tab'
                $ottyPanes = Get-OttyIds 'pane'
            }

            # One tab already exists, so a split leaves -Splits + 1 panes in it, while -Tabs is
            # the total the window should end up with.
            $extra = if ($Mode -eq 'tabs') {
                if ($app -eq 'otty') { [math]::Max(0, $Tabs - $ottyTabs.Count) } else { $Tabs - 1 }
            }
            else { $Splits }
            $want = if ($Mode -eq 'tabs') { $Tabs } else { $Splits + 1 }

            # The first prompt and the first frame have to be done before the layout starts: a
            # chord (or otty's IPC call) that arrives during start-up is dropped, and the empty
            # window's footprint is the baseline the additions are measured against.
            [Win]::Focus($window)
            Wait-Quiet $term
            $term.Refresh()
            $baseMb = [math]::Round($term.PrivateMemorySize64 / 1MB, 1)
            # otty reopens its saved session, so the window may already hold a shell that the
            # layout did not start; the count the layout is checked against is relative to it.
            $baseShells = Measure-Shells $term.Id

            Set-Layout $app $Mode $window $extra

            # Every new tab or pane starts a shell, so the process tree is where a lost chord or
            # a refused IPC call shows up.
            $shellWatch = [Diagnostics.Stopwatch]::StartNew()
            $shells = 0
            while ($shellWatch.Elapsed.TotalSeconds -lt 20) {
                $shells = Measure-Shells $term.Id
                if ($shells -ge $baseShells + $extra) { break }
                Start-Sleep -Milliseconds 250
            }

            Wait-Quiet $term
            $term.Refresh()
            $cpu0 = $term.TotalProcessorTime
            Start-Sleep -Seconds $IdleSeconds
            $term.Refresh()
            $privateMb = [math]::Round($term.PrivateMemorySize64 / 1MB, 1)
            $rows += [pscustomobject]@{
                terminal = $app
                run      = $i
                layout   = $Mode
                panes    = $want
                shells   = $shells
                baseShells = $baseShells
                baseMb   = $baseMb
                private  = $privateMb
                addedMb  = [math]::Round(($privateMb - $baseMb) / [math]::Max($extra, 1), 1)
                idleCpu  = [math]::Round(100.0 * ($term.TotalProcessorTime - $cpu0).TotalSeconds /
                    $IdleSeconds, 2)
            }
            if ($shells -lt $baseShells + $extra) {
                Write-Warning ("{0} run {1}: {2} new shells for {3} panes" -f $app, $i,
                    ($shells - $baseShells), $extra)
            }
            Write-Host ("{0} {1}: {2} private {3} MB (+{4} MB each), idle {5}%" -f $app, $i,
                $Mode, $rows[-1].private, $rows[-1].addedMb, $rows[-1].idleCpu)
            if ($app -eq 'otty') { Clear-OttyLayout $Mode $ottyTabs $ottyPanes }
            Close-Terminal $window $found.Pid
            Start-Sleep -Milliseconds 800
            continue
        }

        # Foreground: a background window is composited (and processed) at a throttled rate.
        [Win]::Focus($window)
        Start-Sleep -Milliseconds 300
        $rect = New-Object Win+RECT
        [Win]::GetWindowRect($window, [ref]$rect) | Out-Null
        $term.Refresh()
        $cpu0 = $term.TotalProcessorTime
        $script:peakBytes = 0

        $flood = Start-Flood $app $launch $window $term
        if (-not $flood) { $flood = Wait-Flood $launch.Flood $term 180 }
        if (-not $flood) {
            Write-Warning ("{0} run {1}: no flood result" -f $app, $i)
            Close-Terminal $window $found.Pid
            continue
        }

        # The emitter returns as soon as the pipe takes the last byte; wait until the terminal
        # has drained and painted the backlog (CPU flat for three samples).
        $flat = 0
        $last = $term.TotalProcessorTime
        $settle = [Diagnostics.Stopwatch]::StartNew()
        while ($settle.Elapsed.TotalSeconds -lt 20 -and $flat -lt 3) {
            Start-Sleep -Milliseconds 250
            $term.Refresh()
            if ($term.HasExited) { break }
            $now = $term.TotalProcessorTime
            if (($now - $last).TotalMilliseconds -lt 20) { $flat++ } else { $flat = 0 }
            $last = $now
            if ($term.PrivateMemorySize64 -gt $script:peakBytes) { $script:peakBytes = $term.PrivateMemorySize64 }
        }

        $seconds = $flood.Ms / 1000.0
        $rows += [pscustomobject]@{
            terminal = $app
            run      = $i
            size     = ('{0}x{1}' -f ($rect.R - $rect.L), ($rect.B - $rect.T))
            mbps     = [math]::Round(($flood.Bytes / 1MB) / $seconds, 1)
            cpuS     = [math]::Round(($last - $cpu0).TotalSeconds, 2)
            drainS   = [math]::Round($settle.Elapsed.TotalSeconds, 2)
            endToEnd = [math]::Round($seconds + $settle.Elapsed.TotalSeconds, 2)
            peakMB   = [math]::Round($script:peakBytes / 1MB, 1)
        }
        Write-Host ("{0} {1}: {2} MB/s, cpu {3}s, drain {4}s" -f $app, $i, $rows[-1].mbps,
            $rows[-1].cpuS, $rows[-1].drainS)
        Close-Terminal $window $found.Pid
        Start-Sleep -Milliseconds 800
    }
}

Write-Host ''
if ($rows.Count -eq 0) { Write-Warning 'no measurements'; exit 1 }
$rows | Sort-Object terminal, run | Format-Table -AutoSize | Out-String | Write-Host
