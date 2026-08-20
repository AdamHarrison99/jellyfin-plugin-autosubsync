<#
.SYNOPSIS
    Verifies every vendored tool in agentic/payload.lock.json agrees with the generated manifest,
    the payloads on disk, and the plugin version.
.DESCRIPTION
    Catches the drift cases: a payload rebuilt without regenerating the manifest, a manifest edited
    by hand, a payload modified on disk, a pinned asset whose hash no longer matches what upstream
    serves, and a release cut with a platform missing.
.PARAMETER ReleaseMode
    Promotes "payload missing for a required platform" and version mismatches from warnings to
    failures, confirms every pinned archive has an asset behind it, confirms every manifest.json
    entry still downloads, and reports a pin that has fallen behind upstream. Use before cutting a
    release.
#>
[CmdletBinding()]
param(
    [switch]$ReleaseMode
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'payload-lock.psm1') -Force

$errors = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]

$repoRoot = Get-RepoRoot
$lock = Read-PayloadLock
$payloadRoot = Get-PayloadRoot

function Add-Problem {
    param([string]$Message, [switch]$OnlyReleaseBlocks)

    if ($ReleaseMode -or -not $OnlyReleaseBlocks) { $errors.Add($Message) }
    else { $warnings.Add($Message) }
}

# ! Redirects to files. Merging a native command's stderr raises NativeCommandError under
#   ErrorActionPreference Stop, and gh writes to stderr whenever a release is absent.
function Invoke-Captured {
    param([string]$FilePath, [string[]]$Arguments)

    $outFile = [System.IO.Path]::GetTempFileName()
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            Lines    = @(Get-Content $outFile -ErrorAction SilentlyContinue)
            Error    = [string](Get-Content $errFile -Raw -ErrorAction SilentlyContinue)
        }
    }
    finally {
        Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue
    }
}

# --- Generated manifest vs lock --------------------------------------------

$manifestPath = Get-ManifestPath
if (-not (Test-Path $manifestPath)) {
    $errors.Add('Cli\PayloadManifest.g.cs is missing. Run build-assy.ps1 -ManifestOnly.')
}
else {
    $onDisk = Get-Content -Path $manifestPath -Raw -Encoding UTF8
    $rendered = New-PayloadManifestSource -Lock $lock

    if ($onDisk.Replace("`r`n", "`n") -ne $rendered.Replace("`r`n", "`n")) {
        $errors.Add('Cli\PayloadManifest.g.cs is stale or hand-edited. Run build-assy.ps1 -ManifestOnly.')
    }
    else {
        Write-Host '  manifest: matches the lock' -ForegroundColor Green
    }
}

# --- Plugin version consistency --------------------------------------------

$csprojPath = Join-Path $repoRoot 'Jellyfin.Plugin.AutoSubSync.csproj'
$csproj = Get-Content -Path $csprojPath -Raw -Encoding UTF8

$assemblyVersion = [regex]::Match($csproj, '<AssemblyVersion>([^<]+)</AssemblyVersion>').Groups[1].Value
$fileVersion = [regex]::Match($csproj, '<FileVersion>([^<]+)</FileVersion>').Groups[1].Value

if ($assemblyVersion -ne $fileVersion) {
    $errors.Add("csproj AssemblyVersion '$assemblyVersion' does not match FileVersion '$fileVersion'.")
}

$buildYamlPath = Join-Path $repoRoot 'build.yaml'
$buildYaml = Get-Content -Path $buildYamlPath -Raw -Encoding UTF8
$yamlVersion = [regex]::Match($buildYaml, '(?m)^version:\s*"?([^"\r\n]+)"?').Groups[1].Value.Trim()

if ($yamlVersion -ne $assemblyVersion) {
    Add-Problem "build.yaml version '$yamlVersion' does not match csproj '$assemblyVersion'." -OnlyReleaseBlocks
}
else {
    Write-Host "  plugin version: $assemblyVersion (csproj and build.yaml agree)" -ForegroundColor Green
}

if ($ReleaseMode) {
    $pluginManifestPath = Join-Path $repoRoot 'manifest.json'
    $manifest = Get-Content -Path $pluginManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $versions = @($manifest[0].versions)

    if ($versions.Count -eq 0) {
        $errors.Add('manifest.json has no version entries; a release must add one.')
    }
    elseif ($versions[0].version -ne $assemblyVersion) {
        $errors.Add("manifest.json newest entry is '$($versions[0].version)' but the build is '$assemblyVersion'.")
    }

    # ! Every published entry, not just the newest. Withdrawing a release leaves the older entry
    #   behind, and Jellyfin offers a download that 404s to anyone still pinned to it.
    foreach ($entry in $versions) {
        if (-not $entry.sourceUrl) {
            $errors.Add("manifest.json entry '$($entry.version)' has no sourceUrl.")
            continue
        }

        # The entry being released has no asset until the release is created; the next release
        # checks it. Skipping it is what lets this gate pass before that step.
        if ($entry.version -eq $assemblyVersion) {
            Write-Host "  manifest $($entry.version) : not published yet (this release)" -ForegroundColor Yellow
            continue
        }

        try {
            Invoke-WebRequest -Uri $entry.sourceUrl -Method Head -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop |
                Out-Null
            Write-Host "  manifest $($entry.version) : download resolves" -ForegroundColor Green
        }
        catch {
            $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }

            if ($status) {
                $errors.Add("manifest.json entry '$($entry.version)' points at a $status : $($entry.sourceUrl)")
            }
            else {
                # Fail closed; an unreachable host is not proof the asset is there.
                $errors.Add("Could not reach '$($entry.sourceUrl)' for manifest.json entry '$($entry.version)'.")
            }
        }
    }
}

# --- A tool this repository builds itself ----------------------------------

# ! One tool, one upstream commit, one interpreter series. A payload built from a different
#   one is a different program shipped under the same version number.
function Test-PlatformsAgree {
    param([string]$Name, $Tool)

    $entries = @($Tool.payloads.PSObject.Properties)
    if ($entries.Count -lt 2) { return }

    foreach ($field in @('tag', 'commit')) {
        $values = @($entries | ForEach-Object { [string]$_.Value.$field } | Sort-Object -Unique)
        if ($values.Count -gt 1) {
            $detail = ($entries | ForEach-Object { "$($_.Name)=$($_.Value.$field)" }) -join ', '
            $errors.Add("$Name platforms disagree on '$field': $detail. Rebuild every platform from one source.")
        }
    }

    $seriesSeen = @{}
    foreach ($entry in $entries) {
        $recorded = [string]$entry.Value.python
        if (-not $recorded) {
            Add-Problem "$Name payload '$($entry.Name)' records no Python version. Rebuild it." -OnlyReleaseBlocks
            continue
        }

        $seriesSeen[$entry.Name] = ((($recorded -replace '[^0-9.]', '') -split '\.')[0..1] -join '.')
    }

    $series = @($seriesSeen.Values | Sort-Object -Unique)
    if ($series.Count -gt 1) {
        $detail = ($seriesSeen.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '
        $errors.Add("$Name platforms were frozen on different Python series: $detail. Rebuild them on one series.")
    }
    elseif ($series.Count -eq 1 -and $Tool.buildPython -and $series[0] -ne [string]$Tool.buildPython) {
        $errors.Add("$Name payloads are on Python $($series[0]) but the lock pins $($Tool.buildPython).")
    }
    elseif ($series.Count -eq 1) {
        Write-Host "  $Name platforms agree: Python $($series[0]), $(@($entries)[0].Value.tag)" -ForegroundColor Green
    }
}

function Test-BuiltTool {
    param([string]$Name, $Tool)

    $root = Join-Path $payloadRoot $Name
    $present = @()
    if (Test-Path $root) {
        $present = @(Get-ChildItem -Path $root -Directory | Select-Object -ExpandProperty Name)
    }

    if ($present.Count -eq 0) {
        Add-Problem "No $Name payload is present. Run agentic\tools\build-assy.ps1." -OnlyReleaseBlocks
    }

    foreach ($rid in $present) {
        $dir = Join-Path $root $rid
        $entry = $Tool.payloads.PSObject.Properties | Where-Object { $_.Name -eq $rid }

        if (-not $entry) {
            $errors.Add("$Name payload '$rid' is on disk but absent from the lock. Rebuild it with build-assy.ps1.")
            continue
        }

        $actual = Get-TreeHash -Path $dir
        if ($actual -ne $entry.Value.sha256) {
            $errors.Add("$Name payload '$rid' does not match its recorded hash. It was modified after the build.")
            continue
        }

        if ($entry.Value.tag -ne $Tool.upstream.tag) {
            $errors.Add("$Name payload '$rid' was built from '$($entry.Value.tag)' but the lock pins '$($Tool.upstream.tag)'.")
            continue
        }

        # ! A bundled ffmpeg sets FFMPEG_DIR upstream and overrides the one the plugin supplies.
        $stowaways = @(Get-ChildItem -Path $dir -Recurse -File |
                Where-Object { $_.BaseName -in @('ffmpeg', 'ffprobe') })
        if ($stowaways.Count -gt 0) {
            $errors.Add("$Name payload '$rid' contains a bundled ffmpeg/ffprobe, which overrides Jellyfin's.")
            continue
        }

        if (-not $entry.Value.archiveSha256) {
            $errors.Add("$Name payload '$rid' has no release archive recorded. Rebuild it with build-assy.ps1.")
            continue
        }

        $archive = Join-Path (Get-DistRoot) $entry.Value.archiveName
        if (Test-Path $archive) {
            $archiveHash = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLower()
            if ($archiveHash -ne $entry.Value.archiveSha256) {
                $errors.Add("Release archive '$($entry.Value.archiveName)' does not match its recorded hash.")
                continue
            }
        }
        elseif ($ReleaseMode) {
            $errors.Add("Release archive '$($entry.Value.archiveName)' is not in agentic\dist. Rebuild it.")
            continue
        }

        Write-Host "  $Name $rid : ok ($($entry.Value.sizeMb) MB, $($entry.Value.tag))" -ForegroundColor Green
    }

    Test-PlatformsAgree -Name $Name -Tool $Tool

    foreach ($required in $Tool.requiredRids) {
        if ($present -notcontains $required) {
            Add-Problem "No $Name payload for required platform '$required'." -OnlyReleaseBlocks
        }
    }
}

# --- A tool pinned to somebody else's release ------------------------------

function Test-PinnedTool {
    param([string]$Name, $Tool)

    $expectedVersion = $Tool.upstream.tag -replace '^v', ''
    if ($Tool.version -ne $expectedVersion) {
        $errors.Add("$Name pins tag '$($Tool.upstream.tag)' but records version '$($Tool.version)'.")
    }

    $assets = Get-ToolAssets -Tool $Tool

    foreach ($rid in @($Tool.requiredRids)) {
        $expectedName = $Tool.assetNames.$rid
        if (-not $expectedName) {
            $errors.Add("$Name has no assetNames entry for required platform '$rid'.")
            continue
        }

        if (-not $assets.Contains($rid)) {
            $errors.Add("$Name has no pinned asset for required platform '$rid'. Run pin-$Name.ps1.")
            continue
        }

        $asset = $assets[$rid]

        if ($asset.name -ne $expectedName) {
            $errors.Add("$Name pinned '$($asset.name)' for '$rid' but assetNames says '$expectedName'.")
            continue
        }

        if ($asset.sha256 -notmatch '^[0-9a-f]{64}$') {
            $errors.Add("$Name asset '$($asset.name)' has no usable sha256. Re-run pin-$Name.ps1.")
            continue
        }

        if ([long]$asset.size -le 0) {
            $errors.Add("$Name asset '$($asset.name)' records a size of $($asset.size).")
            continue
        }

        if ($asset.format -notin @('zip', 'tar.gz')) {
            $errors.Add("$Name asset '$($asset.name)' has unsupported format '$($asset.format)'.")
            continue
        }

        Write-Host "  $Name $rid : $($asset.name) ($([math]::Round($asset.size / 1MB, 1)) MB)" -ForegroundColor Green
    }

    foreach ($rid in @($Tool.optionalRids)) {
        if (-not $assets.Contains($rid)) {
            $warnings.Add("$Name has no pinned asset for optional platform '$rid'.")
        }
    }

    if (-not $Tool.verifiedLocally) {
        $warnings.Add("$Name hashes came from GitHub's asset digests. Run pin-$Name.ps1 -Download to verify them locally.")
    }
}

# --- Uploaded assets -------------------------------------------------------

# A pinned hash with no asset behind it ships a plugin that can never fetch anything.
function Test-UploadedAssets {
    param([string]$Name, $Tool)

    $assets = Get-ToolAssets -Tool $Tool
    if ($assets.Count -eq 0) { return }

    $assetTag = Expand-AssetTemplate -Template $Tool.release.assetTag `
        -Version $Tool.version -Tag $Tool.upstream.tag

    $result = Invoke-Captured -FilePath 'gh' -Arguments @(
        'release', 'view', $assetTag, '--repo', $Tool.release.assetRepo,
        '--json', 'assets', '--jq', '.assets[].name')

    if ($result.ExitCode -ne 0) {
        $errors.Add("Release '$assetTag' does not exist on $($Tool.release.assetRepo).")
        return
    }

    $uploaded = $result.Lines

    foreach ($rid in $assets.Keys) {
        $assetName = $assets[$rid].name
        if ($uploaded -notcontains $assetName) {
            $errors.Add("$Name '$rid' has no uploaded asset '$assetName' on '$assetTag'.")
        }
        else {
            Write-Host "  asset $assetName : uploaded" -ForegroundColor Green
        }
    }
}

# --- Is a pin behind upstream? ---------------------------------------------

function Test-PinIsCurrent {
    param([string]$Name)

    $script = Join-Path $PSScriptRoot "pin-$Name.ps1"
    if (-not (Test-Path $script)) {
        $errors.Add("$Name is pinned but $script is missing, so the pin cannot be checked.")
        return
    }

    & $script -Check
    if ($LASTEXITCODE -eq 2) {
        $errors.Add("The $Name pin is behind upstream. Move it with pin-$Name.ps1, or decide not to and re-run.")
    }
    elseif ($LASTEXITCODE -ne 0) {
        $errors.Add("Could not determine whether the $Name pin is current (pin-$Name.ps1 exited $LASTEXITCODE).")
    }
}

# --- Run every tool in the lock --------------------------------------------

foreach ($property in $lock.tools.PSObject.Properties) {
    $name = $property.Name
    $tool = $property.Value

    switch ($tool.acquisition) {
        'built' { Test-BuiltTool -Name $name -Tool $tool }
        'pinned' { Test-PinnedTool -Name $name -Tool $tool }
        default { $errors.Add("Tool '$name' has unknown acquisition '$($tool.acquisition)'.") }
    }
}

if ($ReleaseMode) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        $errors.Add('gh is required in release mode to confirm the pinned assets are uploaded.')
    }
    else {
        foreach ($property in $lock.tools.PSObject.Properties) {
            Test-UploadedAssets -Name $property.Name -Tool $property.Value
        }
    }

    foreach ($property in $lock.tools.PSObject.Properties) {
        if ($property.Value.acquisition -eq 'pinned') {
            Test-PinIsCurrent -Name $property.Name
        }
    }
}

# --- Report ----------------------------------------------------------------

foreach ($warning in $warnings) {
    Write-Host "  WARN  $warning" -ForegroundColor Yellow
}

if ($errors.Count -gt 0) {
    foreach ($item in $errors) {
        Write-Host "  FAIL  $item" -ForegroundColor Red
    }
    exit 1
}

exit 0
