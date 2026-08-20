<#
.SYNOPSIS
    Downloads the pinned seconv build and extracts it under agentic/payload/seconv/<rid>/.
.DESCRIPTION
    The plugin fetches seconv at runtime, so nothing on a dev box has a copy to test against.
    This puts one where the harnesses can find it, verifying the same sha256 the plugin does.
    Re-running is cheap: an extracted payload matching the lock is left alone.
.PARAMETER Rid
    Runtime identifier to fetch. Defaults to the current platform.
.PARAMETER Force
    Re-download and re-extract even when the payload is already present.
.EXAMPLE
    .\agentic\tools\fetch-seconv.ps1
.EXAMPLE
    .\agentic\tools\fetch-seconv.ps1 -Rid linux-x64
#>
[CmdletBinding()]
param(
    [string]$Rid,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'payload-lock.psm1') -Force

$ToolName = 'seconv'

$lock = Read-PayloadLock
$tool = Get-LockTool -Lock $lock -Name $ToolName

if (-not $Rid) { $Rid = Get-CurrentRid }

$asset = $tool.assets.$Rid
if (-not $asset) {
    throw "The lock has no $ToolName asset for '$Rid'. Known: $($tool.assets.PSObject.Properties.Name -join ', ')"
}

$destRoot = Join-Path (Join-Path (Get-PayloadRoot) $ToolName) $Rid
$exeName = if ($Rid -like 'win-*') { "$($tool.binaryName).exe" } else { $tool.binaryName }
$exePath = Join-Path $destRoot $exeName

if ((Test-Path $exePath) -and -not $Force) {
    Write-Host "$ToolName $($tool.version) already present: $exePath" -ForegroundColor Green
    exit 0
}

$cacheDir = Join-Path (Get-PayloadRoot) '_cache'
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
$archivePath = Join-Path $cacheDir $asset.name

$tag = Expand-AssetTemplate -Template $tool.release.assetTag -Version $tool.version -Tag $tool.upstream.tag -Rid $Rid
$url = "https://github.com/$($tool.release.assetRepo)/releases/download/$tag/$($asset.name)"

Write-Host "Fetching $ToolName $($tool.version) for $Rid" -ForegroundColor Cyan
Write-Host "  url  : $url"
Write-Host "  dest : $destRoot"

if (-not (Test-Path $archivePath)) {
    # ! Silences the progress bar. Rendering it per chunk dominates the runtime of the download.
    $previous = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri $url -OutFile $archivePath }
    finally { $ProgressPreference = $previous }
}

$actual = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLower()
if ($actual -ne $asset.sha256) {
    Remove-Item -Force $archivePath -ErrorAction SilentlyContinue
    throw "Checksum mismatch for $($asset.name). Expected $($asset.sha256), got $actual."
}
Write-Host "  sha256 verified" -ForegroundColor Green

Remove-Item -Recurse -Force $destRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $destRoot | Out-Null

if ($asset.format -eq 'zip') {
    Expand-Archive -Path $archivePath -DestinationPath $destRoot -Force
}
else {
    & tar -xzf $archivePath -C $destRoot
    if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $archivePath" }
}

if (-not (Test-Path $exePath)) {
    throw "Extracted archive has no $exeName at $destRoot. The upstream layout changed."
}

if ($Rid -notlike 'win-*') { & chmod +x $exePath }

$size = [math]::Round(((Get-ChildItem -Path $destRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Host "`n$ToolName ready: $exePath" -ForegroundColor Green
Write-Host "  $size MB extracted"
