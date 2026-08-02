param([string]$Name = 'port-war-lobby')

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;using System.Runtime.InteropServices;
public static class PortCapture {
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
 public struct Rect { public int Left,Top,Right,Bottom; }
}
"@

$process = Get-Process DarkColony.App -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $process -or $process.MainWindowHandle -eq 0) {
  throw 'The compiled Dark Colony window is not running.'
}

$rect = New-Object PortCapture+Rect
[PortCapture]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
$bitmap = New-Object Drawing.Bitmap ($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$dc = $graphics.GetHdc()
$ok = [PortCapture]::PrintWindow($process.MainWindowHandle, $dc, 2)
$graphics.ReleaseHdc($dc); $graphics.Dispose()

$directory = Join-Path $PSScriptRoot 'screenshots'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$path = Join-Path $directory ("{0}-{1}.png" -f $Name, (Get-Date -Format 'HHmmss'))
$bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()
if (-not $ok) { throw 'PrintWindow failed.' }
Write-Output $path
