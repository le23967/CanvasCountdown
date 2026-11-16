<#
.SYNOPSIS
  Builds Canvas Countdown for Windows from a clean state and packages one
  installer per architecture.

.DESCRIPTION
  Everything lands in windows/dist, which Git ignores. Like the macOS scripts,
  this reads its own output back and refuses to package anything wrong:

  - the core tests must pass first;
  - each executable must be built for the architecture its installer claims
    (a mislabelled build would refuse to start on the machines it was
    published for, and nothing else here would notice);
  - the version stamped into the app must be the version being released, and
    match the tag when one is given;
  - nothing private or stray may be inside: no calendar fixtures, test
    assemblies, logs or debug symbols;
  - the files the app cannot start without must be present;
  - each installer must exist, carry the same version, and is hashed so the
    published file can be checked byte for byte.

  It does not sign anything: the build is unsigned, and the README says what
  that means for the first launch.

.PARAMETER Tag
  The release tag, e.g. win-v1.0.0. Optional; when given it must agree with
  the version in Directory.Build.props.

.PARAMETER Architectures
  Which builds to make. Both by default: a release covers x64 and arm64.

.PARAMETER Version
  Overrides the version (used by CI to build a second, newer copy for the
  update test). Normally read from Directory.Build.props.

.PARAMETER SimulateFailure
  Builds installers that fail half-way through copying files. Tests only.
#>
param(
    [string] $Tag,
    [string[]] $Architectures = @('x64', 'arm64'),
    [string] $Version,
    [string] $OutputDirectory,
    [switch] $SimulateFailure,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$windowsRoot = Split-Path -Parent $PSScriptRoot
$props = Join-Path $windowsRoot 'Directory.Build.props'
$appProject = Join-Path $windowsRoot 'src\CanvasCountdown.App\CanvasCountdown.App.csproj'
$testProject = Join-Path $windowsRoot 'tests\CanvasCountdown.Core.Tests'
$installerScript = Join-Path $windowsRoot 'installer\CanvasCountdown.iss'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $windowsRoot 'dist'
}

function Fail([string] $message) {
    Write-Host "!! $message" -ForegroundColor Red
    exit 1
}

function Step([string] $message) {
    Write-Host "==> $message"
}

# --- Version -----------------------------------------------------------------
$declared = ([xml](Get-Content $props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $declared) {
    Fail "No <Version> in $props"
}
if (-not $Version) {
    $Version = $declared.Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Fail "Version '$Version' is not three numbers separated by dots"
}
if ($Tag -and $Tag -ne "win-v$Version") {
    Fail "Tag '$Tag' does not match version $Version (expected win-v$Version)"
}
Step "Building Canvas Countdown $Version for $($Architectures -join ', ')"

# --- Clean -------------------------------------------------------------------
Step "Clearing $OutputDirectory"
if (Test-Path $OutputDirectory) {
    Remove-Item -Recurse -Force $OutputDirectory
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# --- Tests -------------------------------------------------------------------
if (-not $SkipTests) {
    Step 'Core tests'
    dotnet test $testProject -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        Fail 'Core tests failed; nothing was packaged'
    }
}

# --- Inno Setup ----------------------------------------------------------------
$iscc = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if (-not $iscc) {
    foreach ($candidate in @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'))) {
        if ($candidate -and (Test-Path $candidate)) {
            $iscc = $candidate
            break
        }
    }
}
if (-not $iscc) {
    Fail 'Inno Setup 6 (ISCC.exe) not found. Install it with: winget install JRSoftware.InnoSetup'
}

$machineTypes = @{ x64 = 0x8664; arm64 = 0xAA64 }
$platforms = @{ x64 = 'x64'; arm64 = 'ARM64' }
$required = @('CanvasCountdown.exe', 'CanvasCountdown.dll', 'CanvasCountdown.Core.dll', 'Microsoft.WinUI.dll', 'Assets\AppIcon.ico', 'Assets\AppIcon-64.png')
$forbidden = @('*.ics', '*.log', '*.pdb', '*Tests*.dll', 'xunit*.dll', 'install-complete')
$summary = @()

function Get-PeMachine([string] $path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $reader = New-Object System.IO.BinaryReader $stream
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            Fail "$path is not a Windows executable"
        }
        return $reader.ReadUInt16()
    }
    finally {
        $stream.Dispose()
    }
}

foreach ($arch in $Architectures) {
    if (-not $machineTypes.ContainsKey($arch)) {
        Fail "Unknown architecture '$arch' (use x64 or arm64)"
    }
    $publishDir = Join-Path $OutputDirectory "app-$arch"

    Step "Publishing $arch"
    dotnet publish $appProject -c Release -r "win-$arch" "-p:Platform=$($platforms[$arch])" "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) {
        Fail "Publishing $arch failed"
    }

    Step "Checking the $arch build"
    $exe = Join-Path $publishDir 'CanvasCountdown.exe'
    foreach ($file in $required) {
        if (-not (Test-Path (Join-Path $publishDir $file))) {
            Fail "$file is missing from the $arch build; the app would not start"
        }
    }
    # The app's own resource index holds its compiled XAML. It has shipped
    # missing once already, and the symptom — the first window failing with
    # "XAML parsing failed" — says nothing about why.
    $appResources = @('resources.pri', 'CanvasCountdown.pri') | Where-Object { Test-Path (Join-Path $publishDir $_) }
    if (-not $appResources) {
        Fail "The $arch build has no resource index of its own (resources.pri); its windows would not load"
    }

    $machine = Get-PeMachine $exe
    if ($machine -ne $machineTypes[$arch]) {
        Fail ("CanvasCountdown.exe in the {0} build is machine type 0x{1:X4}, not 0x{2:X4}" -f $arch, $machine, $machineTypes[$arch])
    }
    Write-Host ("    executable machine type 0x{0:X4} ({1})" -f $machine, $arch)

    $info = (Get-Item $exe).VersionInfo
    $productVersion = ($info.ProductVersion -split '\+')[0]
    if ($productVersion -ne $Version) {
        Fail "The $arch build says it is version '$productVersion', not $Version"
    }
    Write-Host "    version $productVersion"

    $stray = foreach ($pattern in $forbidden) { Get-ChildItem -Path $publishDir -Recurse -File -Filter $pattern }
    if ($stray) {
        $stray | ForEach-Object { Write-Host "    unexpected: $($_.FullName)" }
        Fail "The $arch build carries files that must not ship"
    }
    $files = Get-ChildItem -Path $publishDir -Recurse -File
    $size = ($files | Measure-Object -Property Length -Sum).Sum
    Write-Host ("    no fixtures, test assemblies, logs or symbols; {0} files, {1:N1} MB" -f $files.Count, ($size / 1MB))

    Step "Packaging the $arch installer"
    $defines = @("/DAppVersion=$Version", "/DArch=$arch", "/DSourceDir=$publishDir", "/DOutputDir=$OutputDirectory")
    if ($SimulateFailure) {
        $defines += '/DSimulateFailure'
    }
    & $iscc /Q @defines $installerScript
    if ($LASTEXITCODE -ne 0) {
        Fail "Inno Setup failed for $arch"
    }

    $installer = Join-Path $OutputDirectory "CanvasCountdown-Windows-$Version-$arch-setup.exe"
    if (-not (Test-Path $installer)) {
        Fail "Expected installer $installer was not produced"
    }
    $installerVersion = (Get-Item $installer).VersionInfo.FileVersion
    if (-not $installerVersion.StartsWith($Version)) {
        Fail "The $arch installer says it is version '$installerVersion', not $Version"
    }
    $hash = (Get-FileHash -Algorithm SHA256 $installer).Hash.ToLowerInvariant()
    $length = (Get-Item $installer).Length
    $summary += [pscustomobject]@{ Architecture = $arch; Installer = (Split-Path -Leaf $installer); Bytes = $length; Sha256 = $hash }
}

Write-Host ''
foreach ($entry in $summary) {
    Write-Host "==> $($entry.Installer)"
    Write-Host ("    size:   {0:N1} MB ({1} bytes)" -f ($entry.Bytes / 1MB), $entry.Bytes)
    Write-Host "    sha256: $($entry.Sha256)"
}
$summary | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $OutputDirectory 'installers.json')
