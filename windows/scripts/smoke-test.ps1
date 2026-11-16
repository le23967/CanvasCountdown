<#
.SYNOPSIS
  Launches a built copy in isolation, checks it stays up, and photographs it.

.DESCRIPTION
  The launch is isolated (CANVAS_COUNTDOWN_ISOLATED_LAUNCH=1): an in-memory
  store, no network, no reminders, no startup entry. If the app dies during
  startup, everything that could explain it is collected — the app's own crash
  and diagnostics logs, the Application event log, and the debug output stream
  (where WinUI writes the line and column of a XAML error) — so the failure
  can be read without a Windows machine to hand.
#>
param(
    [Parameter(Mandatory)] [string] $AppExe,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [int] $SettleSeconds = 20
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path
$AppExe = (Resolve-Path $AppExe).Path
$startedAt = Get-Date

# What was actually shipped next to the executable.
Get-ChildItem -Path (Split-Path -Parent $AppExe) -Recurse -File |
    Sort-Object FullName |
    ForEach-Object { '{0,12}  {1}' -f $_.Length, $_.FullName.Substring((Split-Path -Parent $AppExe).Length + 1) } |
    Out-File (Join-Path $OutputDirectory 'published-files.txt')

# Debug output, best effort: a missing DebugView must not fail the test.
$debugLog = Join-Path $OutputDirectory 'debug-output.txt'
$debugView = $null
try {
    $dbgview = Join-Path $env:RUNNER_TEMP 'Dbgview.exe'
    if (-not (Test-Path $dbgview)) {
        Invoke-WebRequest -Uri 'https://live.sysinternals.com/Dbgview.exe' -OutFile $dbgview -UseBasicParsing
    }
    $debugView = Start-Process -FilePath $dbgview -ArgumentList @('/accepteula', '/t', '/g', '/l', "`"$debugLog`"") -PassThru
    Start-Sleep -Seconds 3
}
catch {
    Write-Host "DebugView unavailable: $($_.Exception.Message)"
}

$env:CANVAS_COUNTDOWN_ISOLATED_LAUNCH = '1'
$process = Start-Process -FilePath $AppExe -PassThru
Start-Sleep -Seconds $SettleSeconds

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
$bitmap.Save((Join-Path $OutputDirectory 'launch.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()

$alive = -not $process.HasExited
if ($alive) {
    Stop-Process -Id $process.Id -Force
}
if ($debugView -and -not $debugView.HasExited) {
    Start-Sleep -Seconds 1
    Stop-Process -Id $debugView.Id -Force
}

$isolatedRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'CanvasCountdown-Isolated'
Get-ChildItem -Path $isolatedRoot -Recurse -Include 'crash.log', 'diagnostics.log' -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $OutputDirectory "$($_.BaseName)-$($_.Directory.Parent.Name).log") }

Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $startedAt.AddMinutes(-1) } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -in @('.NET Runtime', 'Application Error', 'Windows Error Reporting') } |
    Format-List TimeCreated, ProviderName, Id, Message |
    Out-File (Join-Path $OutputDirectory 'events.txt')

if (-not $alive) {
    throw "The app exited during startup (exit code $($process.ExitCode)). See events.txt, debug-output.txt and any crash log."
}
Write-Host "The app stayed up for $SettleSeconds seconds."
