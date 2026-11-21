<#
.SYNOPSIS
    Drives OSC 7501 program-status reports through a real pane and checks the pane header.

.DESCRIPTION
    The parser itself is covered by tests/Gui.LogicTests (ProgramStatusTests); this script
    covers the part only a window shows: that a report reaching the PTY turns into the badge
    in the pane header, and that clearing it takes the badge away again.

    Each scenario starts its own Gui instance with TEMP and APPDATA pointed at a scratch
    directory, so the run never touches the real config, and feeds the report by starting a
    pwsh script in the pane (-e) that writes the escape sequence and then holds.

    The badge is read through UI Automation (a WinUI TextBlock exposes its text as the
    element's Name), so the check does not depend on pixels or on where the window lands.
    Only a failing scenario saves a screenshot.

    Scenarios:
      blocked   state=blocked + kind=question + msg  -> badge shows the message
      working   state=working + progress=42          -> badge shows "42%"
      cleared   report, then state=idle              -> badge disappears

    Exit code 0 when every scenario passes, 1 otherwise.

.EXAMPLE
    ./tests/osc7501.ps1

.EXAMPLE
    ./tests/osc7501.ps1 -Exe 'D:\Programs\Gui\Gui.exe' -KeepArtifacts
#>
[CmdletBinding()]
param(
    [string]$Exe = '',
    [int]$TimeoutSeconds = 25,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($Exe)) {
    $Exe = Join-Path $repo 'Gui/Gui/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/Gui.exe'
}
if (-not (Test-Path $Exe)) {
    throw "Gui.exe not found at $Exe; build it or pass -Exe"
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class OscWindow {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
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

$work = Join-Path $env:TEMP ('gui-osc7501-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$tempDir = Join-Path $work 'temp'
$appData = Join-Path $work 'appdata'
New-Item -ItemType Directory -Path $tempDir, $appData, (Join-Path $appData 'gui') -Force | Out-Null

$message = 'Confirm the command?'
$encodedMessage = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($message))

# ---------------- report scripts ----------------

# Written as a single-quoted here-string so nothing is interpolated twice: the pane script
# needs a literal "$esc", not the value this script happens to hold.
$reportTemplate = @'
$esc = [char]27
[Console]::Out.Write("$esc]7501;PAYLOAD$esc\")
[Console]::Out.Flush()
CLEAR
Start-Sleep -Seconds 240
'@

$clearTemplate = @'
while (-not (Test-Path -LiteralPath 'FLAG')) { Start-Sleep -Milliseconds 100 }
[Console]::Out.Write("$esc]7501;state=idle$esc\")
[Console]::Out.Flush()
'@

function New-ReportScript {
    param([string]$Path, [string]$Payload, [string]$ClearFlag = '')

    $clear = ''
    if ($ClearFlag) { $clear = $clearTemplate.Replace('FLAG', $ClearFlag) }
    $body = $reportTemplate.Replace('PAYLOAD', $Payload).Replace('CLEAR', $clear)
    Set-Content -Path $Path -Value $body -Encoding ascii
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
    param([IntPtr]$Handle, [string]$Text, [double]$Seconds, [switch]$Gone)

    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $names = @(Get-TextNames $Handle)
        $present = $names -contains $Text
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

# ---------------- scenarios ----------------

$scenarios = @(
    [pscustomobject]@{
        Name    = 'blocked'
        Payload = "state=blocked:kind=question:app=demo:msg=$encodedMessage"
        Expect  = $message
        Clears  = $false
    },
    [pscustomobject]@{
        Name    = 'working'
        Payload = 'state=working:progress=42'
        Expect  = '42%'
        Clears  = $false
    },
    [pscustomobject]@{
        Name    = 'cleared'
        Payload = "state=blocked:kind=question:app=demo:msg=$encodedMessage"
        Expect  = $message
        Clears  = $true
    }
)

$results = @()

foreach ($scenario in $scenarios) {
    $flag = Join-Path $work ($scenario.Name + '.clear')
    $report = Join-Path $tempDir ($scenario.Name + '.ps1')
    New-ReportScript -Path $report -Payload $scenario.Payload -ClearFlag $(if ($scenario.Clears) { $flag } else { '' })

    $saved = @{ TEMP = $env:TEMP; TMP = $env:TMP; APPDATA = $env:APPDATA; GUI_LOG = $env:GUI_LOG }
    $env:TEMP = $tempDir
    $env:TMP = $tempDir
    $env:APPDATA = $appData
    $env:GUI_LOG = 'debug'
    try {
        $command = 'pwsh -NoProfile -ExecutionPolicy Bypass -File \"' + $report + '\"'
        $process = Start-Process -FilePath $Exe -ArgumentList ('-e "' + $command + '"') -PassThru
    } finally {
        $env:TEMP = $saved.TEMP
        $env:TMP = $saved.TMP
        $env:APPDATA = $saved.APPDATA
        $env:GUI_LOG = $saved.GUI_LOG
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 150
        $process.Refresh()
    } until ($process.MainWindowHandle -ne 0 -or $process.HasExited -or [DateTime]::UtcNow -gt $deadline)

    $handle = $process.MainWindowHandle
    $status = 'FAIL'
    $detail = ''

    if ($handle -eq 0) {
        $detail = 'no window appeared'
    } else {
        [OscWindow]::ShowWindow($handle, 9) | Out-Null
        [OscWindow]::SetWindowPos($handle, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null
        [OscWindow]::SetForegroundWindow($handle) | Out-Null

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
            }
        } else {
            $status = 'ok'
        }

        if ($status -ne 'ok') {
            $shot = Join-Path $work ($scenario.Name + '.png')
            Save-Screenshot -Handle $handle -Path $shot
            if (Test-Path $shot) { $detail += " (screenshot: $shot)" }
        }
    }

    $process.Refresh()
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force }
    }

    $results += [pscustomobject]@{ scenario = $scenario.Name; result = $status; detail = $detail }
    Write-Host ("{0,-8} {1,-4} {2}" -f $scenario.Name, $status, $detail)
    Start-Sleep -Milliseconds 500
}

Write-Host ''
$failed = @($results | Where-Object { $_.result -ne 'ok' }).Count

# The scratch TEMP holds the log this run produced; ERROR lines there mean the feature broke
# the app, not just the badge.
$log = Join-Path $tempDir 'gui-startup.log'
if (Test-Path $log) {
    $errors = @(Get-Content $log | Where-Object { $_ -match 'ERROR|Exception' })
    if ($errors.Count -gt 0) {
        Write-Host 'log errors:'
        $errors | Select-Object -First 8 | ForEach-Object { Write-Host ('  ' + $_) }
        $failed++
    }
}

if ($failed -eq 0) {
    Write-Host 'PASS: the OSC 7501 badge follows the report'
    if (-not $KeepArtifacts) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    exit 0
}

Write-Host "FAIL: $failed problem(s); artifacts in $work"
exit 1
