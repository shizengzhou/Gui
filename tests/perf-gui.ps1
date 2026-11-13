<#
.SYNOPSIS
    Release-side performance check for the GUI process: cold start, shell readiness, idle CPU
    and private memory.

.DESCRIPTION
    The headless harness (tests/Gui.PerfTests) covers the libghostty-vt hot path. This script
    covers what only a real window can show: Windows App SDK start-up, XAML layout, the shell
    hand-off and the idle timers (caret blink, IME anchor). It launches the app the way a user
    does, measures for a few seconds and closes it again.

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
    [switch]$NoBuild,
    [switch]$Save,
    [string]$BaselinePath = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($Exe)) {
    $Exe = Join-Path $root 'Gui/Gui/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/Gui.exe'
}
$baselinePath = if ([string]::IsNullOrEmpty($BaselinePath)) { Join-Path $PSScriptRoot 'perf-gui-baseline.json' } else { $BaselinePath }

$Tolerance = [ordered]@{ startup = 0.50; shell = 0.50; idleCpu = 1.00; privateMb = 0.25 }

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
    $process = Start-Process -FilePath $Exe -PassThru
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
        Start-Sleep -Milliseconds 1500
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
