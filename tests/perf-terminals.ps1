<#
.SYNOPSIS
    Compares Gui's start-up, idle cost and flood throughput with terminals installed on the
    same machine (Windows Terminal through wt.exe, otty through otty.exe).

.DESCRIPTION
    Every terminal is driven the way a user drives it: an empty window with a prompt, then a
    fixed amount of output written by tests/perf-emitter through the pane. Three numbers come
    out of each mode:

    startup  process start -> a visible window of the terminal's own process, then idle CPU
             and private bytes over -IdleSeconds with a prompt on screen.
    emit     a 64 MiB (or -MB) flood through the pane: the emitter's own wall time (the
             pseudoconsole only accepts bytes as fast as the terminal drains them), the
             terminal's CPU while it absorbs and paints, the time until its CPU goes flat
             again, and the peak private bytes during the flood.

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
    ./tests/perf-terminals.ps1 -Mode startup -Terminal gui,wt -DefaultShell
#>
[CmdletBinding()]
param(
    [ValidateSet('gui', 'wt', 'otty')][string[]]$Terminal = @('gui', 'wt', 'otty'),
    [ValidateSet('startup', 'emit')][string]$Mode = 'startup',
    [int]$Runs = 3,
    [int]$MB = 64,
    [int]$IdleSeconds = 10,
    [switch]$DefaultShell,
    [switch]$OttyPaste,
    [string]$GuiExe = '',
    [string]$WtExe = '',
    [string]$OttyExe = ''
)

$ErrorActionPreference = 'Stop'

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
}
"@

# ---------------- paths ----------------

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($GuiExe)) {
    $GuiExe = Join-Path $repoRoot 'Gui/Gui/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/Gui.exe'
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
$emitterExe = Join-Path $emitterProject 'bin/Release/net8.0/emit.exe'
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
    if ($mode -eq 'startup') {
        if ($DefaultShell) {
            switch ($app) {
                'gui'  { return @{ Exe = $GuiExe;  Args = $null } }
                'wt'   { return @{ Exe = $WtExe;   Args = '-w new' } }
                'otty' { return @{ Exe = $OttyExe; Args = 'open' } }
            }
        }
        switch ($app) {
            'gui'  { return @{ Exe = $GuiExe;  Args = '-e "cmd /k"' } }
            'wt'   { return @{ Exe = $WtExe;   Args = '-w new cmd /k' } }
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
