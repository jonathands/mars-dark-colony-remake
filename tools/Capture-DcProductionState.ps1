[CmdletBinding()]
param(
    [int] $ProcessId,
    [ValidateRange(0, 1800)] [int] $WaitForWorldSeconds = 300,
    [ValidateRange(5, 1800)] [int] $DurationSeconds = 180,
    [ValidateRange(25, 1000)] [int] $IntervalMilliseconds = 100,
    [switch] $CaptureActors,
    [string] $OutputDirectory
)

<#+
.SYNOPSIS
Captures read-only dc.exe world/player changes during a production experiment.

.DESCRIPTION
Run this from an elevated PowerShell before or after a match is loaded and it
waits for the fixed dc.exe world pointer (up to -WaitForWorldSeconds). It then
records the world pointer, world
tick, local team's P7, and changed byte ranges from the local player's 0xe30
byte runtime record.  The initial and final player records are also retained.

This tool never writes to the target process, changes game timing, or installs
a debugger breakpoint.  Output stays under artifacts/, which is ignored because
it can contain machine-specific original-runtime state.

.EXAMPLE
.\tools\Capture-DcProductionState.ps1 -DurationSeconds 120
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $OutputDirectory = Join-Path $scriptRoot '..\artifacts\runtime-captures'
}

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator))
{
    # A same-integrity dc16.exe (the normal non-elevated wrapper launch) can
    # be read with the requested VM_READ/query rights without elevation. Keep
    # the warning, but let OpenProcess make the authoritative access decision;
    # this avoids turning a UAC prompt into a prerequisite for every capture.
    Write-Warning 'Capture is running non-elevated; it will work only if the target game has the same integrity level.'
}

if ($ProcessId -eq 0)
{
    # The original install exposes either dc.exe or the colour-depth wrapper
    # dc16.exe. Both images use the same fixed runtime addresses.
    $targets = @(Get-Process -Name dc,dc16 -ErrorAction SilentlyContinue)
    if ($targets.Count -ne 1)
    {
        throw "Expected exactly one dc.exe or dc16.exe process; found $($targets.Count). Supply -ProcessId explicitly if needed."
    }
    $ProcessId = $targets[0].Id
}
else
{
    $target = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $target)
    {
        throw "Process $ProcessId is no longer running; start the protected game and retry without reusing its old PID."
    }
    if ($target.ProcessName -notin @('dc', 'dc16'))
    {
        throw "Process $ProcessId is '$($target.ProcessName)', not dc.exe or dc16.exe."
    }
}

if (-not ('DarkColonyRuntimeCapture.Native' -as [type]))
{
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace DarkColonyRuntimeCapture
{
    public static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadProcessMemory(
            IntPtr process,
            IntPtr address,
            [Out] byte[] buffer,
            IntPtr size,
            out IntPtr bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
'@
}

$processVmRead = 0x0010
$processQueryInformation = 0x0400
$handle = [DarkColonyRuntimeCapture.Native]::OpenProcess(
    $processVmRead -bor $processQueryInformation, $false, $ProcessId)
if ($handle -eq [IntPtr]::Zero)
{
    throw "OpenProcess failed for PID $ProcessId (Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))."
}

function Read-Bytes([Int64] $Address, [int] $Count)
{
    $buffer = [byte[]]::new($Count)
    [IntPtr] $read = [IntPtr]::Zero
    if (-not [DarkColonyRuntimeCapture.Native]::ReadProcessMemory(
            $handle, [IntPtr]::new($Address), $buffer, [IntPtr]::new($Count), [ref] $read) -or
        $read.ToInt64() -ne $Count)
    {
        throw "ReadProcessMemory failed at 0x$('{0:X}' -f $Address) (Win32 error $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))."
    }
    return $buffer
}

function Read-Int32([Int64] $Address)
{
    return [BitConverter]::ToInt32((Read-Bytes $Address 4), 0)
}

function Changed-Ranges([byte[]] $Before, [byte[]] $After)
{
    $ranges = [System.Collections.Generic.List[string]]::new()
    for ($index = 0; $index -lt $After.Length; )
    {
        if ($Before[$index] -eq $After[$index]) { $index++; continue }
        $start = $index
        while ($index -lt $After.Length -and $Before[$index] -ne $After[$index]) { $index++ }
        $count = $index - $start
        $previewCount = [Math]::Min($count, 32)
        $beforeHex = [Convert]::ToHexString($Before[$start..($start + $previewCount - 1)])
        $afterHex = [Convert]::ToHexString($After[$start..($start + $previewCount - 1)])
        $suffix = if ($previewCount -lt $count) { '...' } else { '' }
        $ranges.Add(('0x{0:X3}+{1}: {2}->{3}{4}' -f $start, $count, $beforeHex, $afterHex, $suffix))
    }
    return $ranges -join '; '
}

function Actor-Summary([byte[]] $Table)
{
    # dc.exe keeps 800 world actors at world+0x7D28 with a 0xDC-byte stride.
    # The producer consumes entity/team at +6/+7, remaining source at +0x0C,
    # and the base-rate accumulator at +0x30.
    $stride = 0xdc
    $count = [Math]::Floor($Table.Length / $stride)
    $lines = [System.Collections.Generic.List[string]]::new()
    for ($slot = 0; $slot -lt $count; $slot++)
    {
        $offset = $slot * $stride
        $active = $Table[$offset + 0x2c]
        if ($active -eq 0) { continue }
        $entity = $Table[$offset + 6]
        $team = $Table[$offset + 7]
        $remaining = [BitConverter]::ToInt32($Table, $offset + 0x0c)
        $rateRaw = [BitConverter]::ToInt32($Table, $offset + 0x30)
        $rate = $rateRaw -shr 16
        $lines.Add(('slot={0} entity={1} team={2} remaining={3} rateRaw=0x{4:X8} rate={5}' -f
            $slot, $entity, $team, $remaining, $rateRaw, $rate))
    }
    return $lines
}

$prefix = $null
try
{
    $worldPointerAddress = 0x498f6cL
    $waitStopwatch = [Diagnostics.Stopwatch]::StartNew()
    do
    {
        $world = [Int64][BitConverter]::ToUInt32((Read-Bytes $worldPointerAddress 4), 0)
        if ($world -ne 0) { break }
        if ($waitStopwatch.Elapsed.TotalSeconds -ge $WaitForWorldSeconds)
        {
            throw 'dc.exe world pointer at 0x498F6C is null; no match loaded before the wait expired.'
        }
        Start-Sleep -Milliseconds 250
    }
    while ($true)

    $team = Read-Int32 ($world + 0x7d1c)
    if ($team -lt 0 -or $team -ge 10) { throw "Unexpected local team $team at world+0x7D1C." }
    $playerAddress = $world + ($team * 0xe30)
    $actorTableAddress = $world + 0x7d28
    $actorTableLength = 800 * 0xdc

    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $prefix = Join-Path $OutputDirectory "dc-production-$stamp-pid$ProcessId"
    $initial = Read-Bytes $playerAddress 0xe30
    [IO.File]::WriteAllBytes("$prefix-player-initial.bin", $initial)
    $previousActors = $null
    if ($CaptureActors)
    {
        $previousActors = Read-Bytes $actorTableAddress $actorTableLength
        [IO.File]::WriteAllBytes("$prefix-actors-initial.bin", $previousActors)
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# dc.exe production runtime capture")
    $lines.Add("# pid=$ProcessId world=0x$('{0:X8}' -f $world) localTeam=$team player=0x$('{0:X8}' -f $playerAddress)")
    $lines.Add('# Columns: elapsedMs, worldTick, P7, changed ranges within player +0x000..+0xE2F')
    $previous = $initial
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $DurationSeconds)
    {
        $current = Read-Bytes $playerAddress 0xe30
        $tick = Read-Int32 ($world + 0x530)
        $p7 = [BitConverter]::ToInt32($current, 0xbac)
        $changes = Changed-Ranges $previous $current
        if ($changes.Length -ne 0)
        {
            $lines.Add(('{0}, {1}, {2}, {3}' -f [int]$stopwatch.ElapsedMilliseconds, $tick, $p7, $changes))
        }
        $previous = $current
        if ($CaptureActors)
        {
            $actors = Read-Bytes $actorTableAddress $actorTableLength
            $actorChanges = Changed-Ranges $previousActors $actors
            if ($actorChanges.Length -ne 0)
            {
                $lines.Add(('{0}, {1}, actors, {2}' -f [int]$stopwatch.ElapsedMilliseconds, $tick, $actorChanges))
            }
            foreach ($summary in (Actor-Summary $actors))
            {
                if ($summary -match 'entity=(40|6|14|47|48) ')
                {
                    $lines.Add(('{0}, {1}, actor, {2}' -f [int]$stopwatch.ElapsedMilliseconds, $tick, $summary))
                }
            }
            $previousActors = $actors
        }
        Start-Sleep -Milliseconds $IntervalMilliseconds
    }
    [IO.File]::WriteAllLines("$prefix.log", $lines)
    [IO.File]::WriteAllBytes("$prefix-player-final.bin", $previous)
    if ($CaptureActors) { [IO.File]::WriteAllBytes("$prefix-actors-final.bin", $previousActors) }
    Write-Host "Captured $($lines.Count - 3) changed samples to $prefix.log"
}
catch
{
    # Preserve failures that occur before a world pointer/prefix exists (for
    # example, waiting at the menu) when this helper is launched hidden.
    if (-not (Test-Path $OutputDirectory))
    {
        New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    }
    $errorPath = if ($null -ne $prefix) { "$prefix.error.log" } else {
        Join-Path $OutputDirectory ("dc-production-capture-{0}.error.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    }
    [IO.File]::WriteAllText($errorPath, ($_ | Out-String))
    throw
}
finally
{
    if ($handle -ne [IntPtr]::Zero) { [DarkColonyRuntimeCapture.Native]::CloseHandle($handle) | Out-Null }
}
