#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [string] $DataPath = (Join-Path $PSScriptRoot '..\..\Dark Colony'),
    # Skips the exclusive-mode step, which switches the monitor's mode for 15 seconds.
    [switch] $NoExclusive
)

<#+
.SYNOPSIS
Drives the port's Video panel with posted input and checks what it applies
and saves.

.DESCRIPTION
The port starts on the main menu with a throwaway settings file
(--display-settings), so the player's display.json is never touched. Steps:
1. F10 opens the panel; SCALING -> FIT and VSYNC -> OFF; OK applies and saves.
2. Unless -NoExclusive: MODE -> EXCLUSIVE; OK switches the monitor to its
   desktop mode and asks to keep it; nobody answers, so after 15 seconds it
   reverts to the window.
3. MODE -> FULLSCREEN; OK goes borderless; the panel opens again over the
   fullscreen picture (clicks mapped through the Presentation log line),
   and Cancel closes it.
Each step leaves a screenshot in screenshots/ and the script checks the log
and the saved file, failing on any difference.

.EXAMPLE
.\tools\Test-VideoPanel.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo "src\DarkColony.App\bin\$Configuration\net8.0-windows\DarkColony.App.exe"
if (-not (Test-Path $exe)) { throw "Build the port first: $exe is missing." }
$logDirectory = Join-Path $repo 'artifacts\logs'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$log = Join-Path $logDirectory 'video-panel.log'
$settings = Join-Path $logDirectory 'video-panel-display.json'
Remove-Item -ErrorAction SilentlyContinue $settings
$sendInput = Join-Path $PSScriptRoot 'Send-PortInput.ps1'
$capture = Join-Path $repo 'capture-port-window.ps1'

$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.WorkingDirectory = $repo
$start.UseShellExecute = $false
foreach ($argument in @('--data', (Resolve-Path $DataPath).Path, '--no-dialogs', '--no-music', '--no-video',
        '--display-settings', $settings, '--windowed', '--window-scale', '1', '--view', 'classic', '--confine-cursor', 'off', '--log', $log)) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)
$deadline = (Get-Date).AddSeconds(30)
while (-not $process.HasExited -and $process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 200
    $process.Refresh()
}
Start-Sleep -Milliseconds 1500

$failures = [Collections.Generic.List[string]]::new()
function Send([string[]] $actions) { & $sendInput -ProcessId $process.Id -Actions $actions -LogPath $log }
function Shot([string] $step) {
    Start-Sleep -Milliseconds 300
    $path = & $capture -Name "video-panel-$step" -ProcessId $process.Id
    Write-Output "screenshot $step`: $path"
}
function Expect([string] $pattern, [string] $what) {
    if (-not (Select-String -Path $log -Pattern $pattern -Quiet)) { $failures.Add("$what (log has no '$pattern')") }
}
function Saved { Get-Content -Raw $settings | ConvertFrom-Json }
# Row arrows (MainForm.VideoPanel.cs): right arrow x 372, rows from y 138 every 24.
function Right([int] $row) { "click:380,$(143 + 24 * $row)" }
$ok = 'click:336,384'
$cancel = 'click:376,384'
# The display-mode confirmation's own OK, higher up (MainForm.VideoPanel.cs).
$confirmOk = 'click:336,240'

try {
    Send @('key:F10', 'wait:500')
    Shot '1-open'
    Expect 'Video panel opened' 'F10 opens the panel'
    Send @((Right 2), (Right 4), 'wait:300')
    Shot '2-fit-vsync-off'
    Send @($ok, 'wait:800')
    Expect 'Display: Windowed -> Windowed' 'OK applies the draft'
    if (-not (Test-Path $settings)) { $failures.Add('OK saves the settings file') }
    elseif ((Saved).scale -ne 'fit' -or (Saved).vSync -ne $false) { $failures.Add("saved scale/vsync: $((Saved).scale)/$((Saved).vSync)") }

    if (-not $NoExclusive) {
        Send @('key:F10', 'wait:400', (Right 0), (Right 0), $ok, 'wait:1500')
        Shot '3-keep-mode'
        Expect 'Exclusive fullscreen' 'EXCLUSIVE switches the display mode'
        Start-Sleep -Seconds 16
        Expect 'Display mode not kept; reverting' 'an unanswered confirmation reverts'
        if ((Saved).mode -ne 'windowed') { $failures.Add("after the revert the saved mode is $((Saved).mode)") }
        Send @('key:F10', 'wait:400', (Right 0), (Right 0), $ok, 'wait:1500', $confirmOk, 'wait:500')
        Expect 'Display mode kept' 'OK on the confirmation keeps the mode'
        Send @('key:F10', 'wait:400', (Right 0), $ok, 'wait:1500')
    }

    Send @('key:F10', 'wait:400', (Right 0), $ok, 'wait:1500')
    Expect 'Display: Windowed -> Borderless' 'FULLSCREEN goes borderless'
    Send @('key:F10', 'wait:600')
    Shot '4-fullscreen-panel'
    Send @($cancel, 'wait:400')
    Expect 'Video panel closed without changes' 'Cancel closes the panel over the fullscreen picture'
    if ((Saved).mode -ne 'borderless') { $failures.Add("the saved mode is $((Saved).mode), not borderless") }
}
finally {
    if (-not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
}

Write-Output "log: $log"
Write-Output "settings: $settings"
Get-Content $settings
if (Select-String -Path $log -Pattern '\[ERROR\]' -Quiet) { $failures.Add('the log has errors') }
if ($process.ExitCode -ne 0) { $failures.Add("exit code $($process.ExitCode)") }
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output "FAIL: $_" }
    exit 1
}
Write-Output 'Video panel test passed.'
