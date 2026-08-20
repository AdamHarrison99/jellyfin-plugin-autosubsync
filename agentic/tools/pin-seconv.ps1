<#
.SYNOPSIS
    Pins the vendored seconv build to a SubtitleEdit release asset and records its hashes.
.DESCRIPTION
    Unlike assy-cli, SeConv is published as a ready-made per-platform binary, so nothing is built
    here. This resolves one upstream release, records the name, SHA-256 and size of each platform
    asset into agentic/payload.lock.json, and regenerates Cli/PayloadManifest.g.cs.

    Hashes come from GitHub's own per-asset digest. Pass -Download to fetch each archive and
    recompute the hash locally instead of trusting that digest.
.PARAMETER Tag
    Upstream release tag to pin. Defaults to the newest non-prerelease release carrying SeConv
    assets, which is how the pin is moved forward.
.PARAMETER Check
    Reports whether a newer release is available and exits without writing anything. Exit code 2
    means the pin is behind.
.PARAMETER Download
    Downloads every asset and verifies GitHub's digest against a locally computed SHA-256.
.EXAMPLE
    .\agentic\tools\pin-seconv.ps1 -Check
.EXAMPLE
    .\agentic\tools\pin-seconv.ps1
.EXAMPLE
    .\agentic\tools\pin-seconv.ps1 -Tag v5.2.0 -Download
#>
[CmdletBinding()]
param(
    [string]$Tag,
    [switch]$Check,
    [switch]$Download
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'payload-lock.psm1') -Force

$ToolName = 'seconv'
$headers = @{ 'User-Agent' = 'jellyfin-plugin-autosubsync-pin' }

if ($env:GITHUB_TOKEN) {
    $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN"
}

$lock = Read-PayloadLock
$tool = Get-LockTool -Lock $lock -Name $ToolName
$repo = $tool.release.assetRepo

$wantedRids = @($tool.requiredRids) + @($tool.optionalRids)

function Test-CarriesSeConv {
    param($Release)
    return @($Release.assets | Where-Object { $_.name -like 'SeConv-*' }).Count -gt 0
}

# The newest stable release that actually ships the assets we pin.
function Find-LatestRelease {
    $uri = 'https://api.github.com/repos/' + $repo + '/releases?per_page=100'

    # ! Assign before wrapping. Invoke-RestMethod emits a JSON array as one item, so
    #   @(Invoke-RestMethod ...) yields a single element holding the whole list.
    $response = Invoke-RestMethod -Uri $uri -Method Get -Headers $headers
    $releases = @($response)

    if ($releases.Count -eq 0) {
        throw "The releases listing for $repo came back empty. Check the GitHub rate limit, or set GITHUB_TOKEN."
    }

    foreach ($release in $releases) {
        if ($release.prerelease -or $release.draft) { continue }
        if (Test-CarriesSeConv -Release $release) { return $release }
    }

    throw "No stable release on $repo carries SeConv assets."
}

function Get-Release {
    param([string]$ReleaseTag)

    try {
        return Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/tags/$ReleaseTag" -Headers $headers
    }
    catch {
        throw "Release '$ReleaseTag' not found on $repo."
    }
}

# --- Check mode ------------------------------------------------------------

if ($Check) {
    $latest = Find-LatestRelease

    if ($latest.tag_name -eq $tool.upstream.tag) {
        Write-Host "  seconv pin: $($tool.upstream.tag) is current" -ForegroundColor Green
        exit 0
    }

    Write-Host "  seconv pin: $($tool.upstream.tag) is behind $($latest.tag_name) (published $($latest.published_at))" -ForegroundColor Yellow
    Write-Host "  Run: .\agentic\tools\pin-seconv.ps1 -Tag $($latest.tag_name)" -ForegroundColor Yellow
    exit 2
}

# --- Resolve the release ---------------------------------------------------

if ($Tag) {
    $release = Get-Release -ReleaseTag $Tag
    if (-not (Test-CarriesSeConv -Release $release)) {
        throw "Release '$Tag' carries no SeConv assets."
    }
}
else {
    $release = Find-LatestRelease
}

$Tag = $release.tag_name
$version = $Tag -replace '^v', ''

Write-Host "Pinning $ToolName" -ForegroundColor Cyan
Write-Host "  upstream : $($tool.upstream.repo) @ $Tag"
Write-Host "  version  : $version"

# --- Resolve one asset per platform ----------------------------------------

$scratch = Join-Path $env:TEMP 'autosubsync-pin-seconv'
if ($Download) { New-Item -ItemType Directory -Force -Path $scratch | Out-Null }

$assets = [ordered]@{}
$missing = @()

foreach ($rid in $wantedRids) {
    $expectedName = $tool.assetNames.$rid
    if (-not $expectedName) {
        throw "No assetNames entry for '$rid'. Add one before pinning."
    }

    $asset = $release.assets | Where-Object { $_.name -eq $expectedName } | Select-Object -First 1
    if (-not $asset) {
        $missing += "$rid ($expectedName)"
        continue
    }

    # ! GitHub's digest is the pin's trust root unless -Download recomputes it here.
    if ($asset.digest -notmatch '^sha256:([0-9a-f]{64})$') {
        throw "Asset '$expectedName' has no usable sha256 digest. Re-run with -Download."
    }
    $sha = $Matches[1]

    if ($Download) {
        $local = Join-Path $scratch $expectedName
        if (-not (Test-Path $local)) {
            Write-Host "  downloading $expectedName ..."
            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $local -Headers $headers
        }

        $localSha = (Get-FileHash -Path $local -Algorithm SHA256).Hash.ToLower()
        if ($localSha -ne $sha) {
            throw "Asset '$expectedName' hashes to $localSha locally but GitHub reports $sha."
        }
        if ((Get-Item $local).Length -ne $asset.size) {
            throw "Asset '$expectedName' is $((Get-Item $local).Length) bytes locally, $($asset.size) per GitHub."
        }
        Write-Host "  verified $expectedName" -ForegroundColor Green
    }

    $assets[$rid] = [ordered]@{
        name   = $expectedName
        sha256 = $sha
        size   = $asset.size
        format = if ($expectedName -like '*.tar.gz') { 'tar.gz' } else { 'zip' }
    }

    $mb = [math]::Round($asset.size / 1MB, 1)
    Write-Host "  $rid : $expectedName ($mb MB)" -ForegroundColor Green
}

foreach ($required in $tool.requiredRids) {
    if (-not $assets.Contains($required)) {
        throw "Release '$Tag' has no asset for required platform '$required'."
    }
}

if ($missing.Count -gt 0) {
    Write-Warning "No asset for optional platform(s): $($missing -join ', ')"
}

# --- Record ----------------------------------------------------------------

$tool.upstream.tag = $Tag
$tool.version = $version
$tool.assets = $assets
$tool | Add-Member -NotePropertyName pinnedUtc `
    -NotePropertyValue ((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')) -Force
$tool | Add-Member -NotePropertyName verifiedLocally -NotePropertyValue ([bool]$Download) -Force

Write-PayloadLock -Lock $lock
Write-PayloadManifest -Lock $lock

$total = 0
foreach ($rid in $assets.Keys) { $total += $assets[$rid].size }
Write-Host "`nPinned $ToolName $version" -ForegroundColor Green
Write-Host "  $($assets.Count) platform assets, $([math]::Round($total / 1MB, 1)) MB total"
Write-Host "  lock:     $(Get-LockPath)"
Write-Host "  manifest: $(Get-ManifestPath)"

if (-not $Download) {
    Write-Host "`nHashes came from GitHub's asset digests. Re-run with -Download to verify them locally." -ForegroundColor Yellow
}
