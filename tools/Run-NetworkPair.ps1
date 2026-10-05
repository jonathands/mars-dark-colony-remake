#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [string] $DataPath = (Join-Path $PSScriptRoot '..\..\Dark Colony'),
    [ValidateRange(5, 600)] [int] $Seconds = 20,
    [string[]] $HostActions = @(),
    [string[]] $ClientActions = @(),
    [string] $Name = 'net',
    # Extra arguments for both peers; they come last and win (for example
    # '--view', '1280x720' to check that network games stay classic).
    [string[]] $ExtraArguments = @()
)

<#+
.SYNOPSIS
Runs two port instances on this machine as a Multi Player War pair.

.DESCRIPTION
The host opens MULTI PLAYER WAR and picks ACT AS SERVER. The client opens
MULTI PLAYER WAR, picks CONNECT TO SERVER, and connects to the default
127.0.0.1. The host then presses READY. Both play in lockstep for
-Seconds, with optional extra -HostActions/-ClientActions (Send-PortInput
syntax) sent once the game starts. Then both windows are captured and closed,
and the network lines of both logs are printed.

.EXAMPLE
.\tools\Run-NetworkPair.ps1 -Seconds 30
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo "src\DarkColony.App\bin\$Configuration\net8.0-windows\DarkColony.App.exe"
if (-not (Test-Path $exe)) { throw "Build the port first: $exe is missing." }
$logDirectory = Join-Path $repo 'artifacts\logs'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null

function Start-Peer([string] $role) {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.WorkingDirectory = $repo
    $start.UseShellExecute = $false
    foreach ($argument in @('--data', (Resolve-Path $DataPath).Path, '--no-dialogs', '--no-music', '--no-video',
            '--windowed', '--window-scale', '1', '--view', 'classic', '--confine-cursor', 'off', '--log', (Join-Path $logDirectory "$Name-$role.log")) + $ExtraArguments) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    $deadline = (Get-Date).AddSeconds(30)
    while (-not $process.HasExited -and $process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    }
    Start-Sleep -Milliseconds 1500
    return $process
}

$input = Join-Path $PSScriptRoot 'Send-PortInput.ps1'
$hostPeer = Start-Peer 'host'
# Main menu MULTI PLAYER WAR, then netopte ACT AS SERVER.
& $input -ProcessId $hostPeer.Id -Actions 'click:407,326', 'wait:800', 'click:543,389', 'wait:800'
$clientPeer = Start-Peer 'client'
# MULTI PLAYER WAR, CONNECT TO SERVER, then getsvre CONNECT.
& $input -ProcessId $clientPeer.Id -Actions 'click:407,326', 'wait:800', 'click:543,421', 'wait:800', 'click:337,347', 'wait:2000'
# multie READY on the host.
& $input -ProcessId $hostPeer.Id -Actions 'click:574,463', 'wait:2500'
if ($HostActions.Count -gt 0) { & $input -ProcessId $hostPeer.Id -Actions $HostActions }
if ($ClientActions.Count -gt 0) { & $input -ProcessId $clientPeer.Id -Actions $ClientActions }
Start-Sleep -Seconds $Seconds

foreach ($peer in @(@{ Role = 'host'; Process = $hostPeer }, @{ Role = 'client'; Process = $clientPeer })) {
    if (-not $peer.Process.HasExited) {
        $shot = & (Join-Path $repo 'capture-port-window.ps1') -Name "$Name-$($peer.Role)" -ProcessId $peer.Process.Id
        Write-Output "$($peer.Role) screenshot: $shot"
        $peer.Process.CloseMainWindow() | Out-Null
        if (-not $peer.Process.WaitForExit(5000)) { $peer.Process.Kill() }
    }
    Write-Output "--- $($peer.Role) log"
    Get-Content (Join-Path $logDirectory "$Name-$($peer.Role).log") |
        Select-String -Pattern 'Hosting|Connected|joined|Joined|Multi Player War|Desync|Connection|Network|rror'
}
