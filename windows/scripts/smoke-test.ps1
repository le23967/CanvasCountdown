<#
.SYNOPSIS
  Launches a built copy in isolation, checks it stays up, and photographs it.

.DESCRIPTION
  The launch is isolated (CANVAS_COUNTDOWN_ISOLATED_LAUNCH=1): an in-memory
  store, no network, no reminders, no startup entry. If the app dies during
  startup, the crash log and the Application event log entries are collected
  so the failure can be read without a Windows machine to hand.
#>
param(
    [Parameter(Mandatory)] [string] $AppExe,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [int] $SettleSeconds = 20
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$startedAt = Get-Date

$env:CANVAS_COUNTDOWN_ISOLATED_LAUNCH = '1'
$process = Start-Process -FilePath (Resolve-Path $AppExe) -PassThru
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

$isolatedRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'CanvasCountdown-Isolated'
Get-ChildItem -Path $isolatedRoot -Recurse -Filter 'crash.log' -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item $_.FullName (Join-Path $OutputDirectory "crash-$($_.Directory.Parent.Name).log") }

Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $startedAt.AddMinutes(-1) } -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -in @('.NET Runtime', 'Application Error', 'Windows Error Reporting') } |
    Format-List TimeCreated, ProviderName, Id, Message |
    Out-File (Join-Path $OutputDirectory 'events.txt')

if (-not $alive) {
    throw "The app exited during startup (exit code $($process.ExitCode)). See events.txt and any crash log."
}
Write-Host "The app stayed up for $SettleSeconds seconds."
