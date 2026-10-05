#Requires -Version 7.0
[CmdletBinding()]
param(
    [int] $ProcessId,
    [Parameter(Mandatory)] [string[]] $Actions,
    [string] $LogPath
)

<#+
.SYNOPSIS
Posts mouse and keyboard input to the running port without moving the real
cursor or stealing focus.

.DESCRIPTION
Coordinates are logical game coordinates (640x480, or the gameplay view).
Without -LogPath they are posted as window coordinates, which is right for a
640x480 window. With -LogPath, each action first reads the port's latest
"Presentation:" log line and maps the point to the pixel the scaled picture
shows it at. Each action is one of:
  move:X,Y     click:X,Y     rclick:X,Y     shiftclick:X,Y
  drag:X1,Y1,X2,Y2           key:<System.Windows.Forms.Keys name>
  sweep:X1,Y1,X2,Y2,MS       wait:<milliseconds>
drag posts eight moves 40 ms apart. sweep holds the left button for MS
milliseconds while it moves back and forth between the two points every few
milliseconds, as a hand on a real mouse does.

.EXAMPLE
.\tools\Send-PortInput.ps1 -Actions 'click:563,446','wait:2000','key:F3'
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class PortInput {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
}
"@

$process = if ($ProcessId) { Get-Process -Id $ProcessId } else { Get-Process DarkColony.App | Select-Object -First 1 }
if (-not $process -or $process.MainWindowHandle -eq 0) { throw 'The compiled Dark Colony window is not running.' }
# The Direct3D surface is the form's only child and fills the 640x480 client area.
$surface = [PortInput]::FindWindowEx($process.MainWindowHandle, [IntPtr]::Zero, [NullString]::Value, [NullString]::Value)
if ($surface -eq [IntPtr]::Zero) { throw 'Game surface window not found.' }

$WM_MOUSEMOVE = 0x200; $WM_LBUTTONDOWN = 0x201; $WM_LBUTTONUP = 0x202
$WM_RBUTTONDOWN = 0x204; $WM_RBUTTONUP = 0x205; $WM_KEYDOWN = 0x100; $WM_KEYUP = 0x101
$MK_LBUTTON = 0x1; $MK_RBUTTON = 0x2; $MK_SHIFT = 0x4

# The output pixel at the centre of a logical pixel (DisplayLayout.ToOutput).
$script:layout = $null
function Read-Layout {
    if (-not $LogPath -or -not (Test-Path $LogPath)) { return }
    $line = Select-String -Path $LogPath -Pattern 'Presentation: output \d+x\d+, logical (\d+)x(\d+), destination (-?\d+),(-?\d+) (\d+)x(\d+)' | Select-Object -Last 1
    if ($line) {
        $g = $line.Matches[0].Groups
        $script:layout = @{ LW = [long]$g[1].Value; LH = [long]$g[2].Value; DX = [long]$g[3].Value; DY = [long]$g[4].Value; DW = [long]$g[5].Value; DH = [long]$g[6].Value }
    }
}
function Point([int] $x, [int] $y) {
    if ($script:layout) {
        $l = $script:layout
        $x = $l.DX + [long][Math]::Floor((2 * $x + 1) * $l.DW / (2 * $l.LW))
        $y = $l.DY + [long][Math]::Floor((2 * $y + 1) * $l.DH / (2 * $l.LH))
    }
    [IntPtr](($y -shl 16) -bor ($x -band 0xffff))
}
function Post([uint32] $message, [int] $flags, [int] $x, [int] $y) {
    [PortInput]::PostMessage($surface, $message, [IntPtr]$flags, (Point $x $y)) | Out-Null
    Start-Sleep -Milliseconds 40
}

foreach ($action in $Actions) {
    Read-Layout
    $kind, $value = $action -split ':', 2
    $numbers = if ($value -match '^[\d,]+$') { $value -split ',' | ForEach-Object { [int]$_ } } else { @() }
    switch ($kind) {
        'move' { Post $WM_MOUSEMOVE 0 $numbers[0] $numbers[1] }
        'click' {
            Post $WM_MOUSEMOVE 0 $numbers[0] $numbers[1]
            Post $WM_LBUTTONDOWN $MK_LBUTTON $numbers[0] $numbers[1]
            Post $WM_LBUTTONUP 0 $numbers[0] $numbers[1]
        }
        'shiftclick' {
            Post $WM_MOUSEMOVE $MK_SHIFT $numbers[0] $numbers[1]
            Post $WM_LBUTTONDOWN ($MK_LBUTTON -bor $MK_SHIFT) $numbers[0] $numbers[1]
            Post $WM_LBUTTONUP $MK_SHIFT $numbers[0] $numbers[1]
        }
        'rclick' {
            Post $WM_MOUSEMOVE 0 $numbers[0] $numbers[1]
            Post $WM_RBUTTONDOWN $MK_RBUTTON $numbers[0] $numbers[1]
            Post $WM_RBUTTONUP 0 $numbers[0] $numbers[1]
        }
        'drag' {
            Post $WM_MOUSEMOVE 0 $numbers[0] $numbers[1]
            Post $WM_LBUTTONDOWN $MK_LBUTTON $numbers[0] $numbers[1]
            for ($step = 1; $step -le 8; $step++) {
                $x = $numbers[0] + [int](($numbers[2] - $numbers[0]) * $step / 8)
                $y = $numbers[1] + [int](($numbers[3] - $numbers[1]) * $step / 8)
                Post $WM_MOUSEMOVE $MK_LBUTTON $x $y
            }
            Post $WM_LBUTTONUP 0 $numbers[2] $numbers[3]
        }
        'sweep' {
            Post $WM_MOUSEMOVE 0 $numbers[0] $numbers[1]
            Post $WM_LBUTTONDOWN $MK_LBUTTON $numbers[0] $numbers[1]
            $clock = [Diagnostics.Stopwatch]::StartNew()
            while ($clock.ElapsedMilliseconds -lt $numbers[4]) {
                # A triangle wave between the points, one leg per second.
                $phase = ($clock.ElapsedMilliseconds % 2000) / 1000.0
                $share = if ($phase -le 1) { $phase } else { 2 - $phase }
                $x = $numbers[0] + [int](($numbers[2] - $numbers[0]) * $share)
                $y = $numbers[1] + [int](($numbers[3] - $numbers[1]) * $share)
                [PortInput]::PostMessage($surface, $WM_MOUSEMOVE, [IntPtr]$MK_LBUTTON, (Point $x $y)) | Out-Null
                [Threading.Thread]::Sleep(4)
            }
            Post $WM_LBUTTONUP 0 $numbers[2] $numbers[3]
        }
        'key' {
            $key = [int][System.Windows.Forms.Keys]::Parse([System.Windows.Forms.Keys], $value)
            [PortInput]::PostMessage($surface, $WM_KEYDOWN, [IntPtr]$key, [IntPtr]1) | Out-Null
            Start-Sleep -Milliseconds 40
            [PortInput]::PostMessage($surface, $WM_KEYUP, [IntPtr]$key, [IntPtr]0xC0000001L) | Out-Null
            Start-Sleep -Milliseconds 40
        }
        'wait' { Start-Sleep -Milliseconds ([int]$value) }
        default { throw "Unknown input action '$action'." }
    }
}
