<#
.SYNOPSIS
    Builds the Gui installer: publish the self-contained payload, then compile
    installer/gui.iss into dist\Gui-<version>-<platform>-setup.exe.

.DESCRIPTION
    Runs the unit and ABI test suites first (skip with -SkipTests), publishes with the
    publish profile so the payload really is self-contained, checks the payload for the
    files a broken publish drops, and finally hands it to Inno Setup.

.PARAMETER Version
    Version stamped into the assembly, the installer and the output file name.
    Accepts "1.2.3" and pre-release forms such as "1.2.3-beta.1".

.PARAMETER Platform
    x64 (default), x86 or ARM64.

.PARAMETER Configuration
    Release (default) or Debug. Debug publishes without ReadyToRun.

.PARAMETER SkipTests
    Package without running tests, for a quick local installer.

.EXAMPLE
    ./build-installer.ps1 0.2.0

.EXAMPLE
    ./build-installer.ps1 -Version 1.0.0-rc.1 -Platform x64 -SkipTests
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Version = "0.1.0",

    [ValidateSet("x64", "x86", "ARM64")]
    [string]$Platform = "x64",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repo = $PSScriptRoot
$project = Join-Path $repo "Gui/Gui/Gui.csproj"
$iss = Join-Path $repo "installer/gui.iss"
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

function Write-Detail([string]$text) {
    Write-Host "    $text" -ForegroundColor DarkGray
}

function Fail([string]$text) {
    Write-Host "error: $text" -ForegroundColor Red
    exit 1
}

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) {
        Fail "$what failed with exit code $LASTEXITCODE"
    }
}

# ---------------- input ----------------

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([-.+][0-9A-Za-z.+-]+)?$') {
    Fail "version '$Version' is not a version number (expected 1.2.3 or 1.2.3-beta.1)"
}

# The setup's resource version accepts digits and dots only; "1.2.3-rc.1" becomes "1.2.3".
$fileVersion = ($Version -split '[-+]')[0]

switch ($Platform) {
    "x64" { $rid = "win-x64" }
    "x86" { $rid = "win-x86" }
    default { $rid = "win-arm64" }
}

Write-Host "Gui installer build" -ForegroundColor White
Write-Detail "version       $Version"
Write-Detail "platform      $Platform ($rid)"
Write-Detail "configuration $Configuration"

# ---------------- tools ----------------

Write-Step "Checking the toolchain"

$dotnetVersion = (& dotnet --version)
if ($LASTEXITCODE -ne 0) { Fail "dotnet was not found on PATH" }

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA "Programs/Inno Setup 6/ISCC.exe"),
    "C:\Program Files (x86)/Inno Setup 6/ISCC.exe",
    "C:\Program Files/Inno Setup 6/ISCC.exe"
)
$iscc = $null
foreach ($candidate in $isccCandidates) {
    if (Test-Path $candidate) { $iscc = $candidate; break }
}
if (-not $iscc) {
    $onPath = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($onPath) { $iscc = $onPath.Source }
}
if (-not $iscc) {
    Fail "ISCC.exe (Inno Setup 6) was not found; install it with: winget install --id JRSoftware.InnoSetup -e"
}
Write-Detail "dotnet        $($dotnetVersion.Trim())"
Write-Detail "iscc          $iscc"

# A running Gui.exe holds its own binaries open and makes the publish copy fail.
$running = Get-Process -Name "Gui" -ErrorAction SilentlyContinue
if ($running) {
    Fail "Gui.exe is running (pid $($running.Id -join ', ')); close it before packaging"
}

# ---------------- tests ----------------

if ($SkipTests) {
    Write-Step "Skipping tests (-SkipTests)"
} else {
    Write-Step "Unit tests"
    Invoke-Checked "dotnet test" {
        & dotnet test (Join-Path $repo "tests/Gui.LogicTests") -c $Configuration --nologo -v quiet
    }

    Write-Step "ABI contract test"
    Invoke-Checked "dotnet run" {
        & dotnet run --project (Join-Path $repo "tests/Gui.AbiTests") -c Release
    }
}

# ---------------- publish ----------------

Write-Step "Publishing the payload"

$publishDir = Join-Path $repo "Gui/Gui/bin/$Configuration/net8.0-windows10.0.19041.0/$rid/publish"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# The profile is what sets SelfContained, and -p:Version stamps the assembly so that
# "gui-cli +version" reports the same number the installer does.
$publishArgs = @(
    "publish", $project,
    "-c", $Configuration,
    "-p:Platform=$Platform",
    "-p:PublishProfile=$rid",
    "-p:Version=$Version",
    "--nologo"
)
Invoke-Checked "dotnet publish" { & dotnet @publishArgs }

$required = @(
    "Gui.exe",
    "gui-cli.exe",
    "Gui.pri",
    "App.xbf",
    "ghostty-vt.dll",
    "System.Private.CoreLib.dll",
    "hostfxr.dll"
)
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $publishDir $file))) {
        Fail "the payload is missing $file - the publish did not produce a self-contained folder"
    }
}

$payloadSize = (Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Detail "payload       $([math]::Round($payloadSize / 1MB, 1)) MB"
Write-Detail "directory     $publishDir"

# ---------------- installer ----------------

Write-Step "Compiling the installer"

$dist = Join-Path $repo "dist"
New-Item -ItemType Directory -Path $dist -Force | Out-Null

Invoke-Checked "ISCC" {
    & $iscc $iss "/DAppVersion=$Version" "/DVerFile=$fileVersion" "/DPublishDir=$publishDir" /Q
}

$setup = Join-Path $dist "Gui-$Version-$($Platform.ToLowerInvariant())-setup.exe"
if (-not (Test-Path $setup)) {
    Fail "ISCC finished but $setup does not exist"
}

$setupSize = (Get-Item $setup).Length
Write-Step "Done"
Write-Host "    installer     $setup" -ForegroundColor Green
Write-Host "    size          $([math]::Round($setupSize / 1MB, 1)) MB" -ForegroundColor Green
Write-Host "    elapsed       $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) s" -ForegroundColor Green