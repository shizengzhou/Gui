<#
.SYNOPSIS
    Builds the Gui installers: publish the payloads, then compile installer/gui.iss
    into dist\Gui-<version>-<platform>-setup.exe and dist\Gui-<version>-<platform>-lite-setup.exe.

.DESCRIPTION
    Runs the unit and ABI test suites first (skip with -SkipTests), publishes each
    payload, checks it for the files a broken publish drops, and finally hands it to
    Inno Setup.

    Two packages come out of one version because the .NET 10 runtime is the larger half
    of the payload:
      full   self-contained, runs on a machine with nothing installed
      lite   framework-dependent, so its download is smaller and its setup installs the
             .NET 10 runtime first when the machine does not have it
    Both are the same product (one AppId, one install directory), so installing one
    over the other keeps the installation and the user picks the download that suits
    the machine.

    The architecture is a second axis: Gui.csproj copies the matching native core from
    native\x64 or native\ARM64, and gui.iss turns -Platform into the payload folder, the
    setup file name and the CPUs the setup accepts. A release packages both, which yields
    four setups from one version.

.PARAMETER Version
    Version stamped into the assembly, the installers and their file names.
    Accepts "1.2.3" and pre-release forms such as "1.2.3-beta.1".

.PARAMETER Platform
    One or more architectures to package: x64 (the default) or ARM64. There is no x86
    because the official libghostty-vt builds ship no 32-bit DLL. A release wants both,
    so pass them together: -Platform x64,ARM64.

.PARAMETER Configuration
    Release (default) or Debug. Debug publishes without ReadyToRun.

.PARAMETER Variant
    Which packages to build: both (default), full or lite.

.PARAMETER SkipTests
    Package without running tests, for a quick local installer.

.EXAMPLE
    ./build-installer.ps1 0.2.0

.EXAMPLE
    ./build-installer.ps1 -Version 1.0.0-rc.1 -Platform x64 -SkipTests

.EXAMPLE
    ./build-installer.ps1 0.2.0 -Variant lite

.EXAMPLE
    ./build-installer.ps1 0.2.0 -Platform x64,ARM64
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Version = "0.1.0",

    [string[]]$Platform = @("x64"),

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("both", "full", "lite")]
    [string]$Variant = "both",

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

# A caller that reaches the script through "powershell -File" hands "-Platform x64,ARM64"
# over as the single string "x64,ARM64", which a ValidateSet would reject, so the names are
# split and checked here instead. "-contains" matches case-insensitively, like the RID table.
$Platform = @($Platform | ForEach-Object { $_ -split "," } | Where-Object { $_ })
foreach ($wanted in $Platform) {
    if (@("x64", "ARM64") -notcontains $wanted) { Fail "platform '$wanted' is neither x64 nor ARM64" }
}
$Platform = @($Platform | Select-Object -Unique)
if ($Platform.Count -eq 0) { Fail "no platform left to package" }

# The RID follows the platform, and installer/gui.iss derives the same win-<rid> payload
# folder from the /DPlatform= it is handed.
$ridOf = @{ "x64" = "win-x64"; "ARM64" = "win-arm64" }
$rids = @($Platform | ForEach-Object { $ridOf[$_] })

Write-Host "Gui installer build" -ForegroundColor White
Write-Detail "version       $Version"
Write-Detail "platforms     $($Platform -join ', ') ($($rids -join ', '))"
Write-Detail "configuration $Configuration"
Write-Detail "variants      $Variant"

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
    # The test loads Gui/Gui/native/x64/ghostty-vt.dll into an x64 process, which is what
    # proves Term/GhosttyNative.cs. The arm64 core comes from the same release, headers and
    # build, but an x64 process cannot load it, so it is only checked for its machine type.
    Invoke-Checked "dotnet run" {
        & dotnet run --project (Join-Path $repo "tests/Gui.AbiTests") -c Release
    }
}

# ---------------- publish and installer ----------------

# One version, two packages: "full" carries the .NET 10 runtime in the payload, "lite" is
# framework-dependent and so smaller to download, but it needs that runtime installed.
# Both are the same product - one AppId, one install directory - so installing either
# over the other keeps the installation. Each architecture adds its own pair.
$variants = @(if ($Variant -eq "both") { "full", "lite" } else { $Variant })
$packages = @(
    foreach ($plat in $Platform) {
        foreach ($name in $variants) {
            [pscustomobject]@{ Platform = $plat; Variant = $name }
        }
    }
)
$dist = Join-Path $repo "dist"
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$built = @()

# Reads the PE header's machine type: 0x8664 for x64, 0xAA64 for arm64.
function Get-PEMachine([string]$path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $reader = New-Object System.IO.BinaryReader $stream
        if ($reader.ReadUInt16() -ne 0x5A4D) { Fail "$path is not a PE file" }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset + 4
        return $reader.ReadUInt16()
    } finally {
        $stream.Dispose()
    }
}

foreach ($package in $packages) {
    $plat = $package.Platform
    $rid = $ridOf[$plat]
    $name = $package.Variant
    $platLower = $plat.ToLowerInvariant()
    $lite = $name -eq "lite"
    $label = if ($lite) { "lite" } else { "full" }
    $suffix = if ($lite) { "-lite" } else { "" }

    Write-Step "Publishing the $label $platLower payload"

    # Separate folders: publishing a framework-dependent build into a folder that still
    # holds a self-contained one keeps hostfxr.dll and the runtime DLLs around and
    # silently produces a hybrid payload.
    $folder = if ($lite) { "publish-fdd" } else { "publish" }
    $publishDir = Join-Path $repo "Gui/Gui/bin/$Configuration/net10.0-windows10.0.19041.0/$rid/$folder"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    # The profile is what sets SelfContained, and -p:Version stamps the assembly so that
    # "gui +version" reports the same number the installer does. The lite payload
    # overrides SelfContained and its output folder; WindowsAppSDKSelfContained stays on
    # in Gui.csproj, because an unpackaged WinUI app needs the SDK next to the exe
    # whether or not the .NET runtime travels with it.
    $publishArgs = @(
        "publish", $project,
        "-c", $Configuration,
        "-p:Platform=$plat",
        "-p:PublishProfile=$rid",
        "-p:Version=$Version",
        "--nologo"
    )
    if ($lite) {
        $publishArgs += @("-p:SelfContained=false", "-p:PublishDir=$($publishDir.TrimEnd('\'))\")
    }
    Invoke-Checked "dotnet publish" { & dotnet @publishArgs }

    $required = @(
        "Gui.exe",
        "bin/gui.exe",
        "Gui.pri",
        "App.xbf",
        "ghostty-vt.dll",
        "Microsoft.ui.xaml.dll",
        # TERMINFO_DIRS points inside the app directory, so a payload without the entry
        # leaves vim and other terminfo programs in an MSYS shell without a terminal type.
        "Assets/terminfo/78/xterm-ghostty"
    )
    if ($lite) {
        # The runtime must be absent here, or the app stops depending on the shared one
        # and the smaller download buys nothing.
        foreach ($file in @("hostfxr.dll", "coreclr.dll", "System.Private.CoreLib.dll")) {
            if (Test-Path (Join-Path $publishDir $file)) {
                Fail "the lite payload contains $file - the publish was not framework-dependent"
            }
        }
    } else {
        $required += @("System.Private.CoreLib.dll", "hostfxr.dll")
    }
    foreach ($file in $required) {
        if (-not (Test-Path (Join-Path $publishDir $file))) {
            Fail "the $label $platLower payload is missing $file"
        }
    }

    # Gui.csproj copies native\<Platform>\ghostty-vt.dll, so the core should already fit the
    # payload's architecture. Checking the machine type anyway is the only ARM64-side proof
    # this machine can offer, and it catches a stale folder or a hand-copied DLL.
    $expectedMachine = if ($plat -eq "x64") { 0x8664 } else { 0xAA64 }
    $machine = Get-PEMachine (Join-Path $publishDir "ghostty-vt.dll")
    if ($machine -ne $expectedMachine) {
        Fail ("the $label $platLower payload carries a ghostty-vt.dll of machine 0x{0:X4}, " +
              "expected 0x{1:X4}" -f $machine, $expectedMachine)
    }
    if ($plat -ne "x64" -and -not (Test-Path (Join-Path $publishDir "conpty.dll"))) {
        # Only the x64 tree has Windows Terminal's ConPTY checked in, so an ARM64 payload
        # runs on the system implementation; see ConPty's static constructor.
        Write-Detail "conpty        the system ConPTY (no bundled payload for $platLower)"
    }

    $payloadSize = (Get-ChildItem $publishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Detail "payload       $([math]::Round($payloadSize / 1MB, 1)) MB"
    Write-Detail "directory     $publishDir"

    Write-Step "Compiling the $label $platLower installer"

    $setup = Join-Path $dist "Gui-$Version-$platLower$suffix-setup.exe"
    $isccArgs = @(
        $iss,
        "/DAppVersion=$Version",
        "/DVerFile=$fileVersion",
        "/DPlatform=$platLower",
        "/DPublishDir=$publishDir",
        "/Q"
    )
    if ($lite) { $isccArgs += "/DLite=1" }
    Invoke-Checked "ISCC" { & $iscc @isccArgs }

    if (-not (Test-Path $setup)) {
        Fail "ISCC finished but $setup does not exist"
    }
    $built += [pscustomobject]@{ Path = $setup; Size = (Get-Item $setup).Length }
}

Write-Step "Done"
foreach ($setup in $built) {
    Write-Host "    installer     $($setup.Path)" -ForegroundColor Green
    Write-Host "    size          $([math]::Round($setup.Size / 1MB, 1)) MB" -ForegroundColor Green
}
Write-Host "    elapsed       $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) s" -ForegroundColor Green