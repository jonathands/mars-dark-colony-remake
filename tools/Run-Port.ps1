#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [string] $DataPath = (Join-Path $PSScriptRoot '..\..\Dark Colony'),
    [string[]] $ExtraArguments = @(),
    [string[]] $Actions = @(),
    [ValidateRange(1, 600)] [int] $Seconds = 6,
    [string] $Name = 'run',
    [switch] $Keep,
    [switch] $Media
)

<#+
.SYNOPSIS
Starts the compiled port for an unattended check, captures its window, and
prints the session log.

.DESCRIPTION
Arguments are passed through ProcessStartInfo.ArgumentList, so paths with
spaces (such as "Dark Colony") stay intact. The port runs with --no-dialogs and
--log artifacts\logs\<Name>.log, so any failure ends the process with a
non-zero exit code and a logged stack trace instead of a modal message box.
Optional -Actions are forwarded to Send-PortInput.ps1 after startup.

.EXAMPLE
.\tools\Run-Port.ps1 -Name main-menu
.\tools\Run-Port.ps1 -ExtraArguments '--single-player-war' -Actions 'click:563,446','wait:3000' -Name war
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo "src\DarkColony.App\bin\$Configuration\net8.0-windows\DarkColony.App.exe"
if (-not (Test-Path $exe)) { throw "Build the port first: $exe is missing." }
$logDirectory = Join-Path $repo 'artifacts\logs'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$logPath = Join-Path $logDirectory "$Name.log"

$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.WorkingDirectory = $repo
$start.UseShellExecute = $false
# Unattended runs skip the CD soundtrack and the videos unless -Media asks for them.
$mediaArguments = if ($Media) { @() } else { @('--no-music', '--no-video') }
# A 640x480 window with the classic view, whatever display.json says, so
# -Actions coordinates are window coordinates, and the real pointer is never
# confined. -ExtraArguments come later and win.
$displayArguments = @('--windowed', '--window-scale', '1', '--view', 'classic', '--confine-cursor', 'off')
foreach ($argument in @('--data', (Resolve-Path $DataPath).Path, '--no-dialogs', '--log', $logPath) + $mediaArguments + $displayArguments + $ExtraArguments) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)

$deadline = (Get-Date).AddSeconds(30)
while (-not $process.HasExited -and $process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 200
    $process.Refresh()
}

$screenshot = $null
if (-not $process.HasExited) {
    if ($Actions.Count -gt 0) {
        Start-Sleep -Seconds 2
        & (Join-Path $PSScriptRoot 'Send-PortInput.ps1') -ProcessId $process.Id -Actions $Actions -LogPath $logPath
    }
    $process.WaitForExit($Seconds * 1000) | Out-Null
}
if (-not $process.HasExited) {
    $screenshot = & (Join-Path $repo 'capture-port-window.ps1') -Name $Name
    if (-not $Keep) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
}

Write-Output "pid: $($process.Id)"
Write-Output ("exit: " + $(if ($process.HasExited) { $process.ExitCode } else { 'running' }))
if ($screenshot) { Write-Output "screenshot: $screenshot" }
Write-Output "log: $logPath"
if (Test-Path $logPath) { Get-Content -Path $logPath -Tail 40 }
