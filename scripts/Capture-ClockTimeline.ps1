<#
.SYNOPSIS
  Captures a timeline of MV's ExtrapolatedClock + CarData playhead and computes
  the F1SimHubLive countdown exactly as the plugin/picker do, so we can verify
  the clock against MV Live Timing across session phases (esp. qualifying
  Q1/Q2/Q3 segment resets) WITHOUT needing SimHub running.

  Run this while MultiViewer replays a session. Each row shows MV's raw anchor,
  the freshest CarData frame (playhead), our extrapolated remaining, and the
  fallback (SessionEnd - playhead) so we can see the two diverge/agree.

.PARAMETER Seconds
  How long to capture for. Default 1800 (30 min). Ctrl+C to stop early.

.PARAMETER IntervalMs
  Poll interval. Default 3000.

.PARAMETER Out
  Output CSV path. Default: scripts\clock-timeline-<timestamp>.csv next to this.

.NOTES
  PlaybackLead is 2s to match the plugin. Times shown in UTC (HH:mm:ss).
#>
[CmdletBinding()]
param(
    [int]$Seconds = 1800,
    [int]$IntervalMs = 3000,
    [string]$Base = 'http://localhost:10101/api/v1/live-timing',
    [string]$Out
)

$ErrorActionPreference = 'SilentlyContinue'
if (-not $Out) {
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $Out = Join-Path $PSScriptRoot "clock-timeline-$stamp.csv"
}
$lead = [TimeSpan]::FromSeconds(2)

function Get-Json($url) { try { Invoke-RestMethod -Uri $url -TimeoutSec 2 } catch { $null } }
function HMS([TimeSpan]$ts) {
    if ($ts -lt [TimeSpan]::Zero) { $ts = [TimeSpan]::Zero }
    if ($ts.TotalHours -ge 1) { '{0}:{1:D2}:{2:D2}' -f [int]$ts.TotalHours, $ts.Minutes, $ts.Seconds }
    else { '{0}:{1:D2}' -f $ts.Minutes, $ts.Seconds }
}

"wall_local,session,segment_lap,extrap,anchor_utc,mv_remaining,playhead_utc,our_remaining,fallback_remaining,delta_s" | Out-File -FilePath $Out -Encoding utf8
Write-Host "Capturing MV clock timeline -> $Out (Ctrl+C to stop)" -ForegroundColor Green
Write-Host ("{0,-10} {1,-22} {2,-6} {3,-8} {4,-9} {5,-9} {6,-9} {7}" -f 'time','session','extrap','mvRem','ourRem','fallback','delta','phase') -ForegroundColor Cyan

$deadline = (Get-Date).AddSeconds($Seconds)
$lastRemaining = $null
while ((Get-Date) -lt $deadline) {
    $clock = Get-Json "$Base/ExtrapolatedClock"
    $si    = Get-Json "$Base/SessionInfo"
    $lap   = Get-Json "$Base/LapCount"
    $car   = Get-Json "$Base/CarData"

    if ($clock) {
        $anchor = [datetime]::MinValue
        if ($clock.Utc) { $anchor = [datetime]::Parse($clock.Utc, $null, [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal) }
        $mvRem = [TimeSpan]::Zero
        if ($clock.Remaining) { [TimeSpan]::TryParse($clock.Remaining, [ref]$mvRem) | Out-Null }
        $extrap = [bool]$clock.Extrapolating

        $playhead = [datetime]::MinValue
        if ($car -and $car.Entries -and $car.Entries.Count -gt 0) {
            $lastUtc = ($car.Entries | ForEach-Object { $_.Utc } | Sort-Object | Select-Object -Last 1)
            if ($lastUtc) { $playhead = [datetime]::Parse($lastUtc, $null, [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal) }
        }

        $pos = $playhead - $lead
        $ourRem = $mvRem
        if ($extrap -and $playhead -ne [datetime]::MinValue -and $anchor -ne [datetime]::MinValue) {
            $ourRem = $mvRem - ($pos - $anchor)
        }

        # Fallback: SessionEnd - playhead
        $fallback = [TimeSpan]::Zero
        if ($si -and $si.EndDate -and $si.GmtOffset) {
            $endLocal = [datetime]::Parse($si.EndDate, $null, [System.Globalization.DateTimeStyles]::AssumeLocal)
            $gmt = [TimeSpan]::Zero; [TimeSpan]::TryParse(($si.GmtOffset -replace '^\+',''), [ref]$gmt) | Out-Null
            if ($si.GmtOffset.StartsWith('-')) { $gmt = $gmt.Negate() }
            $endUtc = [datetimeoffset]::new([datetime]::SpecifyKind($endLocal,'Unspecified'), $gmt).UtcDateTime
            if ($playhead -ne [datetime]::MinValue) { $fallback = $endUtc - $pos }
        }

        $delta = if ($lastRemaining) { [math]::Round(($lastRemaining - $ourRem).TotalSeconds, 1) } else { 0 }
        $lastRemaining = $ourRem

        $sess = if ($si.Name) { $si.Name } else { $si.Type }
        $seg = if ($lap.CurrentLap) { "L$($lap.CurrentLap)/$($lap.TotalLaps)" } else { '' }
        $phase = if (-not $extrap) { 'FROZEN/segment-gap?' } else { 'green' }

        $line = '{0},{1},{2},{3},{4:HH:mm:ss},{5},{6:HH:mm:ss},{7},{8},{9}' -f `
            (Get-Date -Format 'HH:mm:ss'), $sess, $seg, $extrap, $anchor, (HMS $mvRem), $playhead, (HMS $ourRem), (HMS $fallback), $delta
        $line | Out-File -FilePath $Out -Append -Encoding utf8

        $color = if (-not $extrap) { 'Yellow' } else { 'Gray' }
        Write-Host ("{0,-10} {1,-22} {2,-6} {3,-8} {4,-9} {5,-9} {6,-9} {7}" -f `
            (Get-Date -Format 'HH:mm:ss'), ($sess -as [string]), $extrap, (HMS $mvRem), (HMS $ourRem), (HMS $fallback), $delta, $phase) -ForegroundColor $color
    }

    Start-Sleep -Milliseconds $IntervalMs
}
Write-Host "Done. Timeline saved to $Out" -ForegroundColor Green
