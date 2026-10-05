#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Human', 'Gray')] [string] $Race = 'Human',
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [string] $DataPath = (Join-Path $PSScriptRoot '..\..\Dark Colony'),
    [ValidateRange(20000, 1000000)] [int] $GrantP7 = 60000,
    [ValidateRange(10, 300)] [int] $TrainSeconds = 40,
    # Display mode under test (docs/DISPLAY_MODES_PLAN.md): the window scale,
    # the scaling mode, and borderless fullscreen. Clicks are mapped through
    # the port's "Presentation:" log line, so the steps stay in game coordinates.
    [ValidateRange(1, 8)] [int] $WindowScale = 1,
    [ValidateSet('integer', 'fit', 'stretch')] [string] $ScaleMode = 'integer',
    [switch] $Fullscreen,
    # The gameplay view: classic, auto or WxH. HUD clicks follow the anchored HUD.
    [string] $View = 'classic'
)

<#+
.SYNOPSIS
Plays a Single Player War through the HUD and checks that every building and
troop of a race gets built, with a screenshot at each step.

.DESCRIPTION
The port starts in the War lobby with --grant-p7, so the whole build tree is
affordable (such a game is not saved). For -Race Gray the local row's race is
toggled first. The local player sits on team 4 of j4play01, the default map,
and the camera is scrolled onto its city.

The steps are:
1. Buy the six buildings that can be bought, through their HUD buttons, in
   prerequisite order: barracks, science lab, robot factory, then the
   level-2 lab and factory on the same buttons, then the research center.
   Each one is counted on its button, then BUILD orders it, and a ship
   delivers it. A screenshot follows each delivery.
2. Count one of each of the nine troops on the Build tab, then press the
   space bar, which is BUILD too.
3. Count the first research button on the Research tab, then BUILD.
4. Wait -TrainSeconds for the queues, then take a last screenshot.

The script then reads the log's "Built", "Trained" and "Research" lines and
fails unless there are six buildings, nine troops and one research, no
animation or sprite errors, and a normal exit. Input is posted to the window, so
the real cursor never moves. Screenshots go to screenshots/, the log to
artifacts/logs/.

.EXAMPLE
.\tools\Test-LiveConstruction.ps1 -Race Gray
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo "src\DarkColony.App\bin\$Configuration\net8.0-windows\DarkColony.App.exe"
if (-not (Test-Path $exe)) { throw "Build the port first: $exe is missing." }
$name = "live-$($Race.ToLowerInvariant())" + $(if ($Fullscreen) { '-fullscreen' } elseif ($WindowScale -ne 1 -or $ScaleMode -ne 'integer') { "-x$WindowScale-$ScaleMode" } else { '' }) +
    $(if ($View -ne 'classic') { "-view-$View" } else { '' })
$logDirectory = Join-Path $repo 'artifacts\logs'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$log = Join-Path $logDirectory "$name.log"
$sendInput = Join-Path $PSScriptRoot 'Send-PortInput.ps1'
$capture = Join-Path $repo 'capture-port-window.ps1'

$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.WorkingDirectory = $repo
$start.UseShellExecute = $false
$display = @($(if ($Fullscreen) { '--fullscreen' } else { '--windowed' }), '--window-scale', "$WindowScale", '--scale-mode', $ScaleMode, '--view', $View, '--confine-cursor', 'off')
foreach ($argument in @('--data', (Resolve-Path $DataPath).Path, '--no-dialogs', '--no-music', '--no-video',
        '--single-player-war', '--grant-p7', "$GrantP7", '--log', $log) + $display) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)
$deadline = (Get-Date).AddSeconds(30)
while (-not $process.HasExited -and $process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 200
    $process.Refresh()
}
Start-Sleep -Milliseconds 1500

$shots = [Collections.Generic.List[string]]::new()
function Send([string[]] $actions) { & $sendInput -ProcessId $process.Id -Actions $actions -LogPath $log }
function Shot([string] $step) {
    Start-Sleep -Milliseconds 300
    $path = & $capture -Name "$name-$step" -ProcessId $process.Id
    $shots.Add($path)
    Write-Output "screenshot $step`: $path"
}

try {
    # multie: the local row's race button, then READY.
    if ($Race -eq 'Gray') { Send @('click:217,29', 'wait:300') }
    Shot '00-lobby'
    Send @('click:574,463', 'wait:3000')
    # A larger gameplay view moves the HUD right and down
    # (GameplayScreen.Anchor: x >= 400 and y >= 392 follow the edges).
    $presentation = Select-String -Path $log -Pattern 'Presentation: .*logical (\d+)x(\d+)' | Select-Object -Last 1
    $extraWidth = [int]$presentation.Matches[0].Groups[1].Value - 640
    $extraHeight = [int]$presentation.Matches[0].Groups[2].Value - 480
    Write-Output "gameplay view: $($extraWidth + 640)x$($extraHeight + 480)"
    function Hud([int] $x, [int] $y) { "click:$($x + $(if ($x -ge 400) { $extraWidth } else { 0 })),$($y + $(if ($y -ge 392) { $extraHeight } else { 0 }))" }
    # Team 4's view starts at (87,14); its city is at (90,21).
    Send (@(1..12 | ForEach-Object { 'key:Up' }) + @(1..4 | ForEach-Object { 'key:Right' }) + @('wait:500'))
    Shot '01-start'

    # The right-hand column of the Build tab (maine group 84 / 53): barracks,
    # science lab, robot factory, research center. The level-2 lab and
    # factory replace their level-1 button.
    $buildings = [ordered]@{
        '02-barracks' = Hud 606 255
        '03-science-lab' = Hud 606 296
        '04-robot-factory' = Hud 606 337
        '05-science-lab-2' = Hud 606 296
        '06-robot-factory-2' = Hud 606 337
        '07-research-center' = Hud 606 378
    }
    # A click counts an item on its button; BUILD (pushb 19) orders it.
    $build = Hud 559 435
    foreach ($step in $buildings.Keys) {
        # A ship delivers each building (command 19): about 150 updates, ten
        # seconds, before what needs it can be bought.
        Send @($buildings[$step], 'wait:300', $build, 'move:258,240', 'wait:11000')
        Shot $step
    }

    # The troop buttons: the left column top to bottom, then the right column's first two.
    $troops = @((Hud 547 132), (Hud 547 173), (Hud 547 214), (Hud 547 255), (Hud 547 296),
        (Hud 547 337), (Hud 547 378), (Hud 606 132), (Hud 606 173))
    foreach ($troop in $troops) { Send @($troop, 'wait:300') }
    # Rest the pointer inside the map view: left at the HUD's edge it would scroll the view.
    Send @('move:258,240', 'wait:300')
    Shot '08-troops-counted'
    Send @('key:Space', 'wait:500')
    Shot '08-troops-ordered'

    # The Research tab's first button, then BUILD, then back to the Build tab.
    Send @((Hud 577 102), 'wait:300', (Hud 547 132), 'wait:300', $build, 'wait:500')
    Shot '08-research'
    Send @((Hud 538 102), 'move:258,240', 'wait:300')
    Start-Sleep -Seconds $TrainSeconds
    Shot '09-troops-trained'
}
finally {
    if (-not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
}

$built = @(Select-String -Path $log -Pattern '\] Built ' | ForEach-Object { $_.Line -replace '^.*\] ', '' })
$trained = @(Select-String -Path $log -Pattern '\] Trained ' | ForEach-Object { $_.Line -replace '^.*\] ', '' })
$researched = @(Select-String -Path $log -Pattern '\] Research item \d+: Completed' | ForEach-Object { $_.Line -replace '^.*\] ', '' })
$rejected = @(Select-String -Path $log -Pattern 'unavailable|rejected|not reserved' | ForEach-Object { $_.Line -replace '^.*\] ', '' })
$errors = @(Select-String -Path $log -Pattern 'Animation error|sprite error|\[ERROR\]' | ForEach-Object { $_.Line -replace '^.*\] ', '' } | Select-Object -Unique)
Write-Output "--- ${Race}: $($built.Count) buildings, $($trained.Count) troops, $($researched.Count) research"
$built | ForEach-Object { Write-Output "  $_" }
$trained | ForEach-Object { Write-Output "  $_" }
$researched | ForEach-Object { Write-Output "  $_" }
$rejected | ForEach-Object { Write-Output "  REJECTED $_" }
$errors | ForEach-Object { Write-Output "  ERROR $_" }
Write-Output "log: $log"
if ($built.Count -ne 6 -or $trained.Count -ne 9 -or $researched.Count -ne 1 -or $errors.Count -ne 0 -or $process.ExitCode -ne 0) {
    Write-Error "Expected 6 buildings, 9 troops, 1 research, no asset errors and a normal exit (exit code $($process.ExitCode))."
    exit 1
}
Write-Output 'Live construction test passed.'
