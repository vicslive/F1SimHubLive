#requires -Version 5.1
<#
.SYNOPSIS
  Verifies that all three F1SimHubLive csproj files declare the same <Version>.

.DESCRIPTION
  v1.4.0 shipped with the picker and plugin csproj Versions still at 1.3.9
  while the installer csproj said 1.4.0. The installer ran, the plugin loaded,
  but their internal FileVersion strings disagreed with the released tag —
  caught only after the fact. v1.5.0 adds this guard to prevent recurrence.

  Scans:
    F1SimHubLive.csproj
    picker\F1SimHubLive.Picker.csproj
    installer\F1SimHubLive.Installer.csproj

  Exits 0 if all three <Version> values match, 1 otherwise.
  Also accepts an optional -Expected to assert all three equal a target string
  (use this right before tagging a release: .\Check-VersionAlignment.ps1 -Expected '1.5.0').

.EXAMPLE
  pwsh .\scripts\Check-VersionAlignment.ps1

.EXAMPLE
  pwsh .\scripts\Check-VersionAlignment.ps1 -Expected 1.5.0
#>
[CmdletBinding()]
param(
    [string]$Expected
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$targets = @(
    @{ Name = 'plugin';    Path = Join-Path $repoRoot 'F1SimHubLive.csproj' }
    @{ Name = 'picker';    Path = Join-Path $repoRoot 'picker\F1SimHubLive.Picker.csproj' }
    @{ Name = 'installer'; Path = Join-Path $repoRoot 'installer\F1SimHubLive.Installer.csproj' }
)

$results = foreach ($t in $targets) {
    if (-not (Test-Path $t.Path)) {
        Write-Error "Missing csproj: $($t.Path)"
        continue
    }
    [xml]$xml = Get-Content -LiteralPath $t.Path
    # PropertyGroup may be a single object or an array; flatten and pick the first
    # PropertyGroup with a non-empty Version child element.
    $pgs = @($xml.Project.PropertyGroup)
    $version = $null
    foreach ($pg in $pgs) {
        $v = $pg.Version
        if ($v -is [string] -and -not [string]::IsNullOrWhiteSpace($v)) { $version = $v; break }
        if ($v -and $v.'#text') { $version = [string]$v.'#text'; break }
    }
    [pscustomobject]@{
        Component = $t.Name
        Path      = (Resolve-Path -LiteralPath $t.Path).Path
        Version   = "$version".Trim()
    }
}

$results | Format-Table -AutoSize | Out-String | Write-Host

$distinct = @($results.Version | Sort-Object -Unique)
if ($distinct.Count -ne 1) {
    Write-Host "FAIL: csproj <Version> values disagree:" -ForegroundColor Red
    $results | ForEach-Object { Write-Host "  $($_.Component): $($_.Version)" -ForegroundColor Red }
    exit 1
}

if ($Expected -and ($distinct[0] -ne $Expected)) {
    Write-Host "FAIL: csproj <Version> '$($distinct[0])' does not match expected '$Expected'." -ForegroundColor Red
    exit 1
}

Write-Host "OK: all three csproj Versions = $($distinct[0])" -ForegroundColor Green
exit 0
