<#
.SYNOPSIS
    Release-side performance check for the GUI process: cold start, shell readiness, idle CPU
    and private memory.

.DESCRIPTION
    Release-side performance check for the GUI process: cold start, shell readiness, idle CPU
    and private memory, plus a churn pass that prices a pane and checks what several panes do
    to the idle timers.

    The headless harness (tests/Gui.PerfTests) covers the libghostty-vt hot path. This script
    covers what only a real window can show: Windows App SDK start-up, XAML layout, the shell
    hand-off, the idle timers (caret blink, IME anchor) and the pane lifecycle.

    Every measured window starts the app with "--no-session-save -e cmd.exe": a saved
    session must neither change the numbers nor be overwritten by the measurement itself.

    Tabs are opened through the CLI ("gui +new-tab"). The split pass drives the pane header
    buttons through UI Automation; when the automation tree does not expose them (a service
    or non-interactive desktop can hide the window's content) that pass is skipped and says
    so rather than failing the run.

    Run it on an idle machine, with no other Gui.exe open, and compare against a baseline
    recorded on the same machine:

        ./tests/perf-gui.ps1 -Save
        ./tests/perf-gui.ps1

    Exit code 1 means a metric moved further than the tolerance. A build is done first unless
    -NoBuild is passed; close any running Gui.exe before that.
#>
[CmdletBinding()]
param(
    [string]$Exe = "",
    [int]$Runs = 3,
    [int]$IdleSeconds = 10,
    [int]$ChurnTabs = 10,
    [int]$ChurnSplits = 4,
    [switch]$SkipChurn,
    [switch]$NoBuild,
    [switch]$Save,
    [string]$BaselinePath = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($Exe)) {
    $Exe = Join-Path $root 'Gui/Gui/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/Gui.exe'
}
$baselinePath = if ([string]::IsNullOrEmpty($BaselinePath)) { Join-Path $PSScriptRoot 'perf-gui-baseline.json' } else { $BaselinePath }

$Tolerance = [ordered]@{ startup = 0.50; shell = 0.50; idleCpu = 1.00; privateMb = 0.25 }

# Churn limits are absolute rather than baseline-relative: they exist to catch a pane that
# costs far more than a terminal should, and a close path that never gives memory back.
# The retained figures include managed wrappers still waiting for a GC, so they are generous.
$Limits = [ordered]@{ tabMb = 8.0; idleCpuTabs = 2.0; paneMb = 8.0; splitRetainedMb = 16.0 }

# Waits until the process has been quiet for a few consecutive samples, so start-up work
# (GPU, glyph cache, theme brushes) is never mistaken for idle cost. Capped so a busy app
# still gets measured instead of stalling the run.
function Wait-Quiet([Diagnostics.Process]$process, [double]$quotaSeconds = 0.005,
    [int]$quietTicks = 2, [double]$capSeconds = 20) {
    $settle = [Diagnostics.Stopwatch]::StartNew()
    $quiet = 0
    $process.Refresh()
    $previous = $process.TotalProcessorTime
    while ($settle.Elapsed.TotalSeconds -lt $capSeconds -and $quiet -lt $quietTicks) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        $used = ($process.TotalProcessorTime - $previous).TotalSeconds
        $previous = $process.TotalProcessorTime
        if ($used -lt $quotaSeconds) { $quiet++ } else { $quiet = 0 }
    }
}

$existing = Get-Process -Name Gui -ErrorAction SilentlyContinue
if ($existing) {
    throw "Gui.exe is already running (pid $($existing[0].Id)); close it before measuring."
}

if (-not $NoBuild) {
    Write-Host 'building Release x64...'
    & dotnet build (Join-Path $root 'Gui/Gui/Gui.csproj') -c Release -p:Platform=x64 -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
}

function Measure-Run {
    $process = Start-Process -FilePath $Exe -ArgumentList '--no-session-save', '-e', 'cmd.exe' -PassThru
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()

        $process.Refresh()
        while ($process.MainWindowHandle -eq 0 -and $watch.Elapsed.TotalSeconds -lt 30) {
            Start-Sleep -Milliseconds 25
            $process.Refresh()
        }
        $startup = $watch.Elapsed.TotalSeconds

        $shell = $null
        while ($watch.Elapsed.TotalSeconds -lt 30) {
            $shell = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id)" -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^(pwsh|powershell|cmd|bash|wsl)\.exe$' } |
                Select-Object -First 1
            if ($shell) { break }
            Start-Sleep -Milliseconds 100
        }
        $shellReady = $watch.Elapsed.TotalSeconds

        # Let the first prompt and the theme settle before sampling idle cost.
        Wait-Quiet $process
        $process.Refresh()
        $cpu0 = $process.TotalProcessorTime
        $idleWatch = [Diagnostics.Stopwatch]::StartNew()
        Start-Sleep -Seconds $IdleSeconds
        $process.Refresh()
        $idleCpu = 100.0 * ($process.TotalProcessorTime - $cpu0).TotalSeconds / $idleWatch.Elapsed.TotalSeconds

        [pscustomobject]@{
            startup  = [math]::Round($startup, 2)
            shell    = [math]::Round($shellReady, 2)
            idleCpu  = [math]::Round($idleCpu, 2)
            privateMb = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
        }
    }
    finally {
        $process.Refresh()
        if (-not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(3000)) { Stop-Process -Id $process.Id -Force }
        }
    }
}

# ---------------- churn: what one pane costs, and what closing it gives back ----------------

function Find-AutomationButtons([IntPtr]$handle, [string]$name) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $byName = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $byType = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $both = New-Object System.Windows.Automation.AndCondition($byName, $byType)
    $visible = @()
    foreach ($item in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $both)) {
        if (-not $item.Current.BoundingRectangle.IsEmpty) { $visible += $item }
    }
    return ,$visible
}

function Invoke-AutomationButton($button) {
    $pattern = $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

# A pane's header buttons (Split/Close Pane) only exist while the pointer is over the pane,
# so the churn pass parks the cursor inside the window before touching them.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public struct PerfRect { public int Left; public int Top; public int Right; public int Bottom; }
public static class PerfWindow {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out PerfRect rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr handle, IntPtr after,
        int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr handle, bool alt);
}
'@

function Move-CursorIntoWindow([IntPtr]$handle) {
    $rect = New-Object PerfRect
    if (-not [PerfWindow]::GetWindowRect($handle, [ref]$rect)) { return }
    # The pane actions only exist for the window under the pointer. A script started from a
    # terminal may not be allowed to take the foreground, so the window is also raised above
    # the other windows for the duration of the pass; the move then reaches it regardless.
    $topmost = New-Object IntPtr(-1)
    $flags = 0x0001 -bor 0x0002 -bor 0x0040 # no size, no move, show
    [PerfWindow]::ShowWindow($handle, 9) | Out-Null # SW_RESTORE
    [PerfWindow]::SetWindowPos($handle, $topmost, 0, 0, 0, 0, $flags) | Out-Null
    # SetForegroundWindow is refused unless the caller owns the foreground; this switch is
    # the OS path a taskbar click uses and is not subject to that restriction.
    [PerfWindow]::SwitchToThisWindow($handle, $true)
    [PerfWindow]::SetForegroundWindow($handle) | Out-Null
    Start-Sleep -Milliseconds 150
    # Nudge the cursor first: SetCursorPos to the position it already occupies produces no
    # mouse move, and WinUI then never raises PointerEntered to reveal the pane actions.
    $x = [int](($rect.Left + $rect.Right) / 2)
    $y = [int](($rect.Top + $rect.Bottom) / 2)
    [PerfWindow]::SetCursorPos($x - 2, $y - 2) | Out-Null
    Start-Sleep -Milliseconds 60
    [PerfWindow]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 300
}

function Measure-ChurnPhase([ValidateSet('tabs', 'splits')][string]$mode) {
    $cli = Join-Path (Split-Path -Parent $Exe) 'bin/gui.exe'
    if ($mode -eq 'tabs' -and -not (Test-Path $cli)) { throw "CLI not found at $cli" }
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

    $process = Start-Process -FilePath $Exe -ArgumentList '--no-session-save', '-e', 'cmd.exe' -PassThru
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $process.Refresh()
        while ($process.MainWindowHandle -eq 0 -and $watch.Elapsed.TotalSeconds -lt 30) {
            Start-Sleep -Milliseconds 25
            $process.Refresh()
        }

        # Shell start-up is CPU work in the app; let it finish before the first reading.
        Wait-Quiet $process 0.02 3 20
        $process.Refresh()
        $baseline = $process.PrivateMemorySize64 / 1MB

        $created = 0
        if ($mode -eq 'tabs') {
            # Tabs created through the CLI are background tabs: they price the pane, its
            # renderer and its CanvasControl without starting a second shell.
            for ($i = 0; $i -lt $ChurnTabs; $i++) {
                & $cli +new-tab | Out-Null
                $created++
                Start-Sleep -Milliseconds 350
            }
            Wait-Quiet $process
        }
        else {
            # The pane header actions only exist once the first frame is built and the
            # pointer is over the surface; retry instead of treating a slow start as a
            # missing feature.
            $split = @()
            for ($attempt = 0; $attempt -lt 12 -and $split.Count -eq 0; $attempt++) {
                Start-Sleep -Milliseconds 250
                $process.Refresh()
                Move-CursorIntoWindow $process.MainWindowHandle
                $split = Find-AutomationButtons $process.MainWindowHandle 'Split Right'
            }
            for ($i = 0; $i -lt $ChurnSplits; $i++) {
                Move-CursorIntoWindow $process.MainWindowHandle
                $split = Find-AutomationButtons $process.MainWindowHandle 'Split Right'
                if ($split.Count -eq 0) { break }
                Invoke-AutomationButton $split[0]
                $created++
                Start-Sleep -Milliseconds 700
            }
            Wait-Quiet $process
        }

        $process.Refresh()
        $withExtras = $process.PrivateMemorySize64 / 1MB

        if ($created -le 0) { return $null }

        if ($mode -eq 'tabs') {
            # The extra tabs stay open: this measures their cost and what the idle timers
            # do with several panes, not a close path (closing needs UI Automation, which
            # is attempted by the split phase below).
            $cpu0 = $process.TotalProcessorTime
            $idleWatch = [Diagnostics.Stopwatch]::StartNew()
            Start-Sleep -Seconds 5
            $process.Refresh()
            $idleCpu = 100.0 * ($process.TotalProcessorTime - $cpu0).TotalSeconds /
                $idleWatch.Elapsed.TotalSeconds
            return [ordered]@{
                added   = ($withExtras - $baseline) / $created
                idleCpu = [math]::Round($idleCpu, 2)
                retained = $null
            }
        }

        while ((Find-AutomationButtons $process.MainWindowHandle 'Close Pane').Count -gt 1) {
            Move-CursorIntoWindow $process.MainWindowHandle
            $panes = Find-AutomationButtons $process.MainWindowHandle 'Close Pane'
            Invoke-AutomationButton $panes[$panes.Count - 1]
            Start-Sleep -Milliseconds 500
        }

        Wait-Quiet $process
        $process.Refresh()
        $retained = $process.PrivateMemorySize64 / 1MB

        return [ordered]@{
            added    = ($withExtras - $baseline) / $created
            idleCpu  = $null
            retained = $retained - $baseline
        }
    }
    finally {
        $process.Refresh()
        if (-not $process.HasExited -and $process.MainWindowHandle -ne 0) {
            # Undo the topmost raise used to reach the pane actions.
            $notTopmost = New-Object IntPtr(-2)
            $flags = 0x0001 -bor 0x0002 -bor 0x0040
            [PerfWindow]::SetWindowPos($process.MainWindowHandle, $notTopmost, 0, 0, 0, 0, $flags) | Out-Null
        }
        if (-not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(3000)) { Stop-Process -Id $process.Id -Force }
        }
    }
}

$samples = @()
for ($i = 1; $i -le $Runs; $i++) {
    Write-Host "run $i/$Runs..."
    $samples += Measure-Run
}

# Best-of, like the headless harness: a single slow start must not look like a regression.
$result = [ordered]@{
    startup   = ($samples | Measure-Object startup -Minimum).Minimum
    shell     = ($samples | Measure-Object shell -Minimum).Minimum
    idleCpu   = ($samples | Measure-Object idleCpu -Minimum).Minimum
    privateMb = ($samples | Measure-Object privateMb -Minimum).Minimum
}

$churn = $null
if (-not $SkipChurn) {
    try {
        Write-Host 'churn: tabs...'
        $tabs = Measure-ChurnPhase 'tabs'
        Write-Host 'churn: splits...'
        $splits = Measure-ChurnPhase 'splits'
        $churn = [ordered]@{
            tabMb       = $tabs.added
            idleCpuTabs = $tabs.idleCpu
        }
        if ($null -ne $splits) {
            $churn.paneMb = $splits.added
            $churn.splitRetainedMb = $splits.retained
        }
        else {
            Write-Host 'churn: pane actions are not exposed by UI Automation here (split churn skipped)'
        }
    }
    catch {
        Write-Host "churn skipped: $($_.Exception.Message)"
    }
}

Write-Host ''
if (-not $Save -and -not (Test-Path $baselinePath)) {
    Write-Host "no baseline at $baselinePath - run once with -Save on this machine first"
}
Write-Host ('{0,-12} {1,10} {2,10} {3,9}  {4}' -f 'metric', 'value', 'baseline', 'delta', 'status')
$failed = $false
foreach ($name in $result.Keys) {
    $value = $result[$name]
    $expected = $null
    $delta = ''
    $status = 'new'

    if (-not $Save -and (Test-Path $baselinePath)) {
        $baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json
        $expected = $baseline.$name
        if ($null -ne $expected -and $expected -gt 0) {
            $change = ($value - $expected) / $expected
            $delta = '{0:+0.0%;-0.0%}' -f $change
            $regressed = $change -gt $Tolerance[$name]
            $status = if ($regressed) { 'REGRESSED' } else { 'ok' }
            if ($regressed) { $failed = $true }
        }
    }

    Write-Host ('{0,-12} {1,10:0.##} {2,10} {3,9}  {4}' -f $name, $value, $expected, $delta, $status)
}

if ($null -ne $churn) {
    Write-Host ''
    Write-Host ('{0,-16} {1,10} {2,10} {3,9}  {4}' -f 'metric', 'value', 'limit', '', 'status')
    foreach ($name in $churn.Keys) {
        $value = [math]::Round($churn[$name], 2)
        $limit = $Limits[$name]
        $ok = $value -le $limit
        if (-not $ok) { $failed = $true }
        $status = if ($ok) { 'ok' } else { 'OVER' }
        Write-Host ('{0,-16} {1,10:0.##} {2,10} {3,9}  {4}' -f $name, $value, $limit, '', $status)
    }
}

if ($Save) {
    $payload = [ordered]@{
        captured = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss')
        exe = [IO.Path]::GetFileNameWithoutExtension($Exe)
        tolerance = $Tolerance
        startup = $result.startup
        shell = $result.shell
        idleCpu = $result.idleCpu
        privateMb = $result.privateMb
    }
    $payload | ConvertTo-Json | Set-Content -Path $baselinePath -Encoding utf8
    Write-Host ''
    Write-Host "baseline written: $baselinePath"
    exit 0
}

Write-Host ''
if ($failed) { Write-Host 'FAIL: a GUI metric regressed'; exit 1 }
Write-Host 'PASS: GUI metrics are within tolerance'
exit 0
