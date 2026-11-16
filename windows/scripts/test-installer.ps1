<#
.SYNOPSIS
  Installs, updates, breaks and uninstalls real copies, and checks each step.

.DESCRIPTION
  The update path is the riskiest thing in the app: done wrong, it leaves
  someone with no working copy. So it is tested the only way that means
  anything — on real installed copies:

    1. a fresh install of the first version;
    2. a silent update to a newer version, exactly as the app runs it
       (/VERYSILENT /RELAUNCH), which must replace the copy and start it again;
    3. an update that fails half-way through copying files, which must leave
       the previous version installed, intact and running again;
    4. an uninstall, which must remove the app, its shortcut, its link
       handler and its startup entry, and leave the user's data alone.

  Every app launched here inherits an isolated environment, so nothing it
  does touches real data.

.PARAMETER FirstInstaller
.PARAMETER NextInstaller
.PARAMETER BrokenInstaller
  Installers for three consecutive versions; the third built with
  -SimulateFailure.
#>
param(
    [Parameter(Mandatory)] [string] $FirstInstaller,
    [Parameter(Mandatory)] [string] $NextInstaller,
    [Parameter(Mandatory)] [string] $BrokenInstaller,
    [Parameter(Mandatory)] [string] $FirstVersion,
    [Parameter(Mandatory)] [string] $NextVersion,
    [Parameter(Mandatory)] [string] $LogDirectory
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
$LogDirectory = (Resolve-Path $LogDirectory).Path
$env:CANVAS_COUNTDOWN_ISOLATED_LAUNCH = '1'

$appDir = Join-Path $env:LOCALAPPDATA 'Programs\Canvas Countdown'
$exe = Join-Path $appDir 'CanvasCountdown.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Canvas Countdown.lnk'
$failures = 0

function Check([bool] $condition, [string] $description) {
    if ($condition) {
        Write-Host "  ok   $description"
    }
    else {
        Write-Host "  FAIL $description" -ForegroundColor Red
        $script:failures++
    }
}

function Run-Installer([string] $installer, [string] $name, [string[]] $extra = @()) {
    $log = Join-Path $LogDirectory "$name.log"
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"") + $extra
    # -PassThru and WaitForExit, not -Wait: -Wait also waits for every child,
    # and an update relaunches the app as the installer's child, which never
    # exits by itself.
    $process = Start-Process -FilePath (Resolve-Path $installer) -ArgumentList $arguments -PassThru
    $process.WaitForExit()
    return $process.ExitCode
}

function Installed-Version {
    if (-not (Test-Path $exe)) {
        return $null
    }
    return ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0]
}

function Running-Copies {
    Get-CimInstance Win32_Process -Filter "Name = 'CanvasCountdown.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($appDir, [StringComparison]::OrdinalIgnoreCase) }
}

function Wait-ForRunningCopy([int] $seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        $running = @(Running-Copies)
        if ($running.Count -gt 0) {
            return $running
        }
        Start-Sleep -Milliseconds 500
    }
    return @()
}

function Stop-RunningCopies {
    Running-Copies | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
}

Write-Host '==> 1. Fresh install'
$code = Run-Installer $FirstInstaller 'install-first'
Check ($code -eq 0) "installer exited 0 (got $code)"
Check ((Installed-Version) -eq $FirstVersion) "installed version is $FirstVersion (got $(Installed-Version))"
Check (Test-Path (Join-Path $appDir 'install-complete')) 'install-complete marker written'
Check (Test-Path $shortcut) 'Start menu shortcut created'
Check (Test-Path 'HKCU:\Software\Classes\canvascountdown\shell\open\command') 'reminder link handler registered'
Check (Test-Path 'HKCU:\Software\Classes\AppUserModelId\le23967.CanvasCountdown') 'notification identity registered'
Check (-not (Test-Path "$appDir.previous")) 'no previous copy left behind'

Write-Host '==> The installed copy starts'
$started = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 12
Check (-not $started.HasExited) 'installed app stays up'
Stop-RunningCopies

Write-Host '==> 2. Update, as the app runs it'
# Leave the old version running, as it would be: the installer must close it.
Start-Process -FilePath $exe | Out-Null
Start-Sleep -Seconds 8
$code = Run-Installer $NextInstaller 'update-next' @('/CLOSEAPPLICATIONS', '/RELAUNCH')
Check ($code -eq 0) "update exited 0 (got $code)"
Check ((Installed-Version) -eq $NextVersion) "installed version is now $NextVersion (got $(Installed-Version))"
Check (-not (Test-Path "$appDir.previous")) 'previous copy removed after a successful update'
Check (Test-Path (Join-Path $appDir 'install-complete')) 'install-complete marker present'
$relaunched = @(Wait-ForRunningCopy)
Check ($relaunched.Count -ge 1) 'app relaunched after the update'
Check (@($relaunched | Where-Object { $_.CommandLine -like '*--updated*' }).Count -ge 1) 'relaunch says it was updated'
Stop-RunningCopies

Write-Host '==> 3. An update that fails half-way'
Start-Process -FilePath $exe | Out-Null
Start-Sleep -Seconds 8
$hashBefore = (Get-FileHash $exe).Hash
$code = Run-Installer $BrokenInstaller 'update-broken' @('/CLOSEAPPLICATIONS', '/RELAUNCH')
Check ($code -ne 0) "broken update reported failure (exit code $code)"
Check ((Installed-Version) -eq $NextVersion) "still on $NextVersion (got $(Installed-Version))"
Check ((Get-FileHash $exe).Hash -eq $hashBefore) 'the executable is byte for byte the one from before'
Check (Test-Path (Join-Path $appDir 'install-complete')) 'restored copy is marked complete'
Check (-not (Test-Path "$appDir.previous")) 'no previous copy left behind after restoring'
Check (-not (Test-Path (Join-Path $appDir 'simulated-failure'))) 'nothing from the failed update remains'
$restored = @(Wait-ForRunningCopy)
Check ($restored.Count -ge 1) 'the previous version was started again'
Check (@($restored | Where-Object { $_.CommandLine -like '*--update-failed*' }).Count -ge 1) 'it was told the update failed'
Stop-RunningCopies

Write-Host '==> 4. Uninstall'
$dataDir = Join-Path $env:LOCALAPPDATA 'CanvasCountdown'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
Set-Content -Path (Join-Path $dataDir 'keep-me.txt') -Value 'user data'
$uninstaller = Join-Path $appDir 'unins000.exe'
Check (Test-Path $uninstaller) 'uninstaller present'
$uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $LogDirectory 'uninstall.log')`"") -PassThru
$uninstall.WaitForExit()
$deadline = (Get-Date).AddSeconds(60)
while ((Test-Path $appDir) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
Check (-not (Test-Path $appDir)) 'app folder removed'
Check (-not (Test-Path $shortcut)) 'Start menu shortcut removed'
Check (-not (Test-Path 'HKCU:\Software\Classes\canvascountdown')) 'link handler removed'
Check (-not (Test-Path 'HKCU:\Software\Classes\AppUserModelId\le23967.CanvasCountdown')) 'notification identity removed'
$run = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'CanvasCountdown' -ErrorAction SilentlyContinue
Check ($null -eq $run) 'no startup entry left'
Check (Test-Path (Join-Path $dataDir 'keep-me.txt')) "the user's data was left alone"

Write-Host ''
if ($failures -gt 0) {
    throw "$failures installer check(s) failed. Logs: $LogDirectory"
}
Write-Host 'All installer checks passed.'
