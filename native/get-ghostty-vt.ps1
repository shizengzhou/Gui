<#
.SYNOPSIS
    Installs the official prebuilt libghostty-vt Windows binaries into Gui\Gui\native\<platform>.

.DESCRIPTION
    libghostty-vt is released separately from Ghostty, with its own vt-<version> tags, and
    each release publishes a zip per Windows architecture:

        https://release.files.ghostty.org/libghostty-vt/<version>/ghostty-vt-<version>-x86_64-windows-gnu.zip
        https://release.files.ghostty.org/libghostty-vt/<version>/ghostty-vt-<version>-aarch64-windows-gnu.zip

    A zip holds bin/ghostty-vt.dll plus its pdb, the import and static libraries, the C
    headers and the CMake/pkg-config files. Gui talks to the DLL through P/Invoke, so only
    the DLL is installed; Gui.csproj copies it next to Gui.exe.

    Every zip is checked against the release's SHA256SUMS before anything is copied, so a
    truncated or redirected download cannot land in the tree. That proves the archive matches
    the checksum file, not that the checksum file is genuine; to close that gap, verify its
    minisign signature with the project's public key and re-run with that file:

        curl.exe -LO <zip-url> <zip-url>.minisig .../SHA256SUMS
        minisign -V -P RWQlAjJC23149WL2sEpT/l0QKy7hMIFhYdQOFy0Z7z7PbneUgvlsnYcV -m SHA256SUMS
        ./native/get-ghostty-vt.ps1 -Version <version> -SumsFile SHA256SUMS

    There is no 32-bit zip, which is why the installer script has no x86 either. Upstream
    tests the x86_64 zip at release; the aarch64 zip is built the same way but never run, so
    treat an ARM64 payload as unverified until it has been started on Arm hardware.

.PARAMETER Version
    The libghostty-vt release to install, such as 1.0.1. The default is the version this
    build of Gui is developed and ABI-tested against.

.PARAMETER Platform
    Which architectures to install: x64 (the default) and ARM64, or both.

.PARAMETER SumsFile
    A SHA256SUMS already downloaded and, if you checked it, minisign-verified. Without it
    the script fetches the file from the release directory.

.EXAMPLE
    ./native/get-ghostty-vt.ps1

.EXAMPLE
    ./native/get-ghostty-vt.ps1 -Version 1.0.2 -Platform x64,ARM64
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Version = "1.0.1",

    [string[]]$Platform = @("x64", "ARM64"),

    [string]$SumsFile
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Fail([string]$text) {
    Write-Host "error: $text" -ForegroundColor Red
    exit 1
}

# "powershell -File" passes "-Platform x64,ARM64" as the single string "x64,ARM64", which a
# ValidateSet would reject, so the names are split and checked here instead.
$Platform = @($Platform | ForEach-Object { $_ -split "," } | Where-Object { $_ })
foreach ($wanted in $Platform) {
    if (@("x64", "ARM64") -notcontains $wanted) { Fail "platform '$wanted' is neither x64 nor ARM64" }
}
$Platform = @($Platform | Select-Object -Unique)

# The project's own platform name -> the architecture token in the release file name.
$archOf = @{ "x64" = "x86_64-windows-gnu"; "ARM64" = "aarch64-windows-gnu" }

$base = "https://release.files.ghostty.org/libghostty-vt/$Version"
$nativeDir = Join-Path $PSScriptRoot "..\Gui\Gui\native"

# The sums file lists every release file; one "<sha256>  <name>" line per archive.
function Get-ExpectedHash([string]$sumsPath, [string]$fileName) {
    foreach ($line in Get-Content $sumsPath) {
        $parts = $line -split '\s+', 2
        if ($parts.Count -ne 2) { continue }
        # Some sums files prefix a name with '*' for a binary-mode check; drop it.
        $name = $parts[1].TrimStart('*')
        if ($name -ieq $fileName) { return $parts[0].ToLowerInvariant() }
    }
    return $null
}

$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("gvt-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp | Out-Null

try {
    $sumsPath = $SumsFile
    if (-not $sumsPath) {
        $sumsPath = Join-Path $temp "SHA256SUMS"
        Write-Host ">> fetching SHA256SUMS for $Version"
        Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile $sumsPath -UseBasicParsing
    }

    $installed = @()
    foreach ($plat in $Platform) {
        $zipName = "ghostty-vt-$Version-$($archOf[$plat]).zip"
        $zipPath = Join-Path $temp $zipName

        Write-Host ">> fetching $zipName"
        Invoke-WebRequest -Uri "$base/$zipName" -OutFile $zipPath -UseBasicParsing

        $expected = Get-ExpectedHash $sumsPath $zipName
        if (-not $expected) { Fail "$zipName is not listed in SHA256SUMS for $Version" }
        $actual = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            Fail "sha256 mismatch for ${zipName}: expected $expected, got $actual"
        }
        Write-Host "    sha256 ok  $actual"

        $expanded = Join-Path $temp "x-$plat"
        Expand-Archive -Path $zipPath -DestinationPath $expanded -Force

        # The archive unpacks to one directory named after the zip; take the DLL from its bin.
        $dll = Get-ChildItem -Path $expanded -Recurse -Filter "ghostty-vt.dll" |
            Select-Object -First 1
        if (-not $dll) { Fail "$zipName does not contain ghostty-vt.dll" }

        $target = Join-Path $nativeDir "$plat\ghostty-vt.dll"
        New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
        if ((Test-Path $target) -and
            (Get-FileHash -Path $target -Algorithm SHA256).Hash.ToLowerInvariant() -eq
            (Get-FileHash -Path $dll.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) {
            Write-Host ("    {0,-10} already up to date" -f $plat)
        } else {
            Copy-Item -Path $dll.FullName -Destination $target -Force
            Write-Host ("    {0,-10} {1} ({2:N1} MB)" -f $plat, $target,
                ((Get-Item $target).Length / 1MB))
        }
        $installed += [pscustomobject]@{ Platform = $plat; Path = $target }
    }

    Write-Host ""
    Write-Host ">> libghostty-vt $Version installed for: $($installed.Platform -join ', ')"
    Write-Host "   run the ABI contract test before committing:"
    Write-Host "     dotnet run --project tests/Gui.AbiTests -c Release"
} finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}
