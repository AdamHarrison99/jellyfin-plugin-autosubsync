<#
.SYNOPSIS
    Builds the vendored assy-cli payload for the current platform and records it in the lock file.
.DESCRIPTION
    PyInstaller cannot cross-compile, so this must be run once per target platform. Each run
    updates one entry under tools['assy-cli'].payloads in agentic/payload.lock.json.
.PARAMETER Tag
    Upstream tag to build. Defaults to the tag pinned in the lock file. Passing a different tag
    is how the bundled dependency is upgraded.
.PARAMETER Python
    Path to the interpreter to build with. Defaults to the newest installed version the upstream
    dependency tree publishes wheels for. The resolved version is recorded in the lock.
.PARAMETER PayloadVersion
    The payload's own revision, e.g. 2.0. It names the release tag and the archives, and it keys
    the plugin's on-disk payload cache, so it must be bumped whenever the built bytes change --
    including when only this repository's wrapper changes and upstream stays put. Defaults to
    whatever the lock already records.
.PARAMETER ManifestOnly
    Re-renders Cli/PayloadManifest.g.cs from the lock and exits. Needs no Python or network.
.EXAMPLE
    .\agentic\tools\build-assy.ps1
.EXAMPLE
    .\agentic\tools\build-assy.ps1 -Tag v6.5
.EXAMPLE
    .\agentic\tools\build-assy.ps1 -ManifestOnly
#>
[CmdletBinding()]
param(
    [string]$Tag,
    [string]$Rid,
    [string]$WorkDir,
    [string]$Python,
    [string]$PayloadVersion,
    [switch]$SkipSmokeTest,
    [switch]$KeepWorkDir,
    [switch]$ManifestOnly
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'payload-lock.psm1') -Force

$ToolName = 'assy-cli'

function Assert-Command {
    param([string]$Name, [string]$Hint)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name is required but was not found on PATH. $Hint"
    }
}

# Newest interpreter the upstream dependency tree publishes wheels for, newest first.
$SupportedPythons = @('3.13', '3.12', '3.11', '3.10')

function Get-PythonVersion {
    param([string]$Path)
    $raw = & $Path -c "import sys; print('.'.join(map(str, sys.version_info[:3])))" 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return ([string]$raw).Trim()
}

function Get-PythonSeries {
    param([string]$Version)
    return (($Version -split '\.')[0..1] -join '.')
}

# ! Assignment alone throws on a PSCustomObject that lacks the property.
function Set-LockProperty {
    param($Target, [string]$Name, $Value)

    if ($Target.PSObject.Properties.Name -contains $Name) { $Target.$Name = $Value }
    else { $Target | Add-Member -NotePropertyName $Name -NotePropertyValue $Value }
}

# ! A newer interpreter is not a better one. ffsubsync pulls webrtcvad-wheels, which ships
#   prebuilt wheels only up to cp313; beyond that pip falls back to needing a C compiler.
function Resolve-BuildPython {
    param([string]$Explicit, [string]$PinnedSeries)

    $wanted = if ($PinnedSeries) { @($PinnedSeries) } else { $SupportedPythons }

    if ($Explicit) {
        $version = Get-PythonVersion -Path $Explicit
        if (-not $version) { throw "The interpreter at '$Explicit' did not run." }
        return [pscustomobject]@{ Path = $Explicit; Version = $version }
    }

    if (Get-Command py -ErrorAction SilentlyContinue) {
        foreach ($candidate in $wanted) {
            $probe = & py "-$candidate" -c "import sys; print(sys.executable)" 2>$null
            if ($LASTEXITCODE -eq 0 -and $probe) {
                $path = ([string]$probe).Trim()
                return [pscustomobject]@{ Path = $path; Version = (Get-PythonVersion -Path $path) }
            }
        }
    }

    foreach ($name in @('python3', 'python')) {
        $onPath = (Get-Command $name -ErrorAction SilentlyContinue).Source
        if (-not $onPath) { continue }

        $found = Get-PythonVersion -Path $onPath
        if ($found -and $wanted -contains (Get-PythonSeries -Version $found)) {
            return [pscustomobject]@{ Path = $onPath; Version = $found }
        }
    }

    throw "No Python $($wanted -join ' or ') was found. Install one and re-run, or pass -Python <path>."
}

# ! Redirects to files. Merging a native command's stderr with 2>&1 raises NativeCommandError
#   under ErrorActionPreference Stop, which hides the real message behind a PowerShell one.
function Invoke-Captured {
    param([string]$FilePath, [string[]]$Arguments, [string]$WorkDir)

    $outFile = Join-Path $WorkDir 'capture-out.txt'
    $errFile = Join-Path $WorkDir 'capture-err.txt'

    $process = Start-Process -FilePath $FilePath -ArgumentList $Arguments -NoNewWindow -Wait -PassThru `
        -RedirectStandardOutput $outFile -RedirectStandardError $errFile

    $text = (Get-Content $outFile -Raw -ErrorAction SilentlyContinue) +
            (Get-Content $errFile -Raw -ErrorAction SilentlyContinue)

    return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = [string]$text }
}

$lock = Read-PayloadLock

if ($ManifestOnly) {
    Write-PayloadManifest -Lock $lock
    Write-Host "Wrote $(Get-ManifestPath)" -ForegroundColor Green
    exit 0
}

$tool = Get-LockTool -Lock $lock -Name $ToolName
$binaryName = $tool.binaryName

if (-not $Tag) { $Tag = $tool.upstream.tag }
if (-not $Rid) { $Rid = Get-CurrentRid }

# ! The payload's own revision, ¬upstream's version. It keys the plugin's payload cache, so a
#   rebuild that keeps the old number is never fetched by a server that already holds one.
$version = if ($PayloadVersion) { $PayloadVersion } else { [string]$tool.version }
if (-not $version) {
    throw 'No payload version. Pass -PayloadVersion, or record one under tools.assy-cli.version.'
}

# ! GetTempPath, not $env:TEMP. That variable is unset on Linux and this script builds there too.
if (-not $WorkDir) { $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) 'autosubsync-payload-build' }

$destRoot = Join-Path (Join-Path (Get-PayloadRoot) $ToolName) $Rid
$srcDir = Join-Path $WorkDir 'AutoSubSync'
$venvDir = Join-Path $WorkDir 'buildenv'
$distDir = Join-Path $WorkDir 'dist'

Write-Host "Building assy-cli payload" -ForegroundColor Cyan
Write-Host "  payload  : $version"
Write-Host "  upstream : $($tool.upstream.repo) @ $Tag"
Write-Host "  rid      : $Rid"
Write-Host "  work dir : $WorkDir"
Write-Host "  dest     : $destRoot"

Assert-Command git 'Install git and re-run.'

# ! Every platform's payload must freeze the same interpreter series. A payload built on a
#   different one is a different runtime wearing the same version number.
$pinnedSeries = if ($tool.PSObject.Properties.Name -contains 'buildPython') { [string]$tool.buildPython } else { $null }

$hostPython = Resolve-BuildPython -Explicit $Python -PinnedSeries $pinnedSeries
$hostSeries = Get-PythonSeries -Version $hostPython.Version

if ($pinnedSeries -and $hostSeries -ne $pinnedSeries) {
    throw "The lock pins Python $pinnedSeries for $ToolName and this is $($hostPython.Version). Every platform's payload must freeze the same series. Install Python $pinnedSeries, or change buildPython in payload.lock.json deliberately and rebuild every platform."
}

Write-Host "  python   : $($hostPython.Version) ($($hostPython.Path))"
if (-not $pinnedSeries) {
    Write-Host "  pinning Python $hostSeries for every platform of $ToolName" -ForegroundColor Yellow
}

# --- Source ----------------------------------------------------------------

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

if (Test-Path (Join-Path $srcDir '.git')) {
    Write-Host "`n[1/6] Fetching upstream" -ForegroundColor Cyan
    & git -C $srcDir fetch --tags --depth 1 origin $Tag
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed for tag $Tag" }
    & git -C $srcDir checkout --force FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw "git checkout failed for tag $Tag" }
}
else {
    Write-Host "`n[1/6] Cloning upstream" -ForegroundColor Cyan
    Remove-Item -Recurse -Force $srcDir -ErrorAction SilentlyContinue
    & git clone --depth 1 --branch $Tag $tool.upstream.repo $srcDir
    if ($LASTEXITCODE -ne 0) { throw "git clone failed for tag $Tag" }
}

$commit = (& git -C $srcDir rev-parse HEAD).Trim()

$upstreamEntry = Join-Path $srcDir ($tool.upstream.entryPoint -replace '/', '\')
if (-not (Test-Path $upstreamEntry)) {
    throw "Entry point not found: $upstreamEntry. Upstream layout changed; update payload.lock.json."
}

# ! The freeze runs our wrapper, which hands every upstream subcommand to upstream's own main().
#   Freezing upstream's cli.py directly instead drops the vad subcommand the audio check needs.
$wrapperDir = Join-Path $PSScriptRoot 'assy-entry'
$entryPoint = Join-Path $wrapperDir 'assy_cli_entry.py'
foreach ($required in @($entryPoint, (Join-Path $wrapperDir 'assy_vad.py'))) {
    if (-not (Test-Path $required)) { throw "Wrapper source not found: $required" }
}

# --- Build environment -----------------------------------------------------

Write-Host "`n[2/6] Creating build virtualenv" -ForegroundColor Cyan
Remove-Item -Recurse -Force $venvDir -ErrorAction SilentlyContinue
& $hostPython.Path -m venv $venvDir
if ($LASTEXITCODE -ne 0) { throw 'Failed to create the build virtualenv.' }

if ($Rid -like 'win-*') {
    $venvPython = Join-Path $venvDir 'Scripts\python.exe'
}
else {
    $venvPython = Join-Path $venvDir 'bin/python'
}

& $venvPython -m pip install --upgrade pip wheel | Out-Host

$requirements = Join-Path $srcDir 'requirements.txt'
if (Test-Path $requirements) {
    & $venvPython -m pip install -r $requirements | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'pip install of upstream requirements failed.' }
}

# ! Installs the project, not just a requirements file. Upstream declares its dependencies in
#   pyproject.toml, and a freeze built without them crashes on first import.
$projectMetadata = @('pyproject.toml', 'setup.py', 'setup.cfg') |
    ForEach-Object { Join-Path $srcDir $_ } |
    Where-Object { Test-Path $_ }

if (-not $projectMetadata -and -not (Test-Path $requirements)) {
    throw "No requirements.txt, pyproject.toml, setup.py or setup.cfg at $srcDir. Nothing declares the dependencies to freeze."
}

if ($projectMetadata) {
    & $venvPython -m pip install $srcDir | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'pip install of the upstream project failed.' }
}

& $venvPython -m pip install pyinstaller | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'pip install pyinstaller failed.' }

Write-Host "`n[3/6] Recording resolved dependency versions" -ForegroundColor Cyan
$freeze = & $venvPython -m pip freeze
$resolved = [ordered]@{}
foreach ($line in $freeze) {
    if ($line -match '^([A-Za-z0-9_.\-]+)==(.+)$') {
        $resolved[$Matches[1].ToLower()] = $Matches[2].Trim()
    }
}
Write-Host "  $($resolved.Count) packages resolved"

# --- Freeze ----------------------------------------------------------------

Write-Host "`n[4/6] Running PyInstaller (onedir)" -ForegroundColor Cyan
Remove-Item -Recurse -Force $distDir -ErrorAction SilentlyContinue

# PyInstaller carries no non-.py file it is not told about. Mirrors upstream's own build.spec,
# minus resources/ffmpeg-bin, which the plugin supplies itself.
$dataSeparator = if ($Rid -like 'win-*') { ';' } else { ':' }
$mainDir = Join-Path $srcDir 'main'
$resourcesDir = Join-Path $mainDir 'resources'

# ! Without this the multiprocessing worker re-launch falls through to argparse and the parent
#   hangs forever. See the hook's own header.
$freezeSupportHook = Join-Path $PSScriptRoot 'pyi_rth_freeze_support.py'
if (-not (Test-Path $freezeSupportHook)) {
    throw "Runtime hook not found: $freezeSupportHook. Without it every ffsubsync run hangs until it is killed."
}

# ! importlib.import_module() is invisible to PyInstaller's analysis. Unlisted, call_ffsubsync is
#   omitted and every sync fails at runtime with "No module named".
$hooksDir = Join-Path $PSScriptRoot 'assy-hooks'
if (-not (Test-Path $hooksDir)) {
    throw "Hook directory not found: $hooksDir"
}

$engineModules = @('call_ffsubsync')
foreach ($module in $engineModules) {
    if (-not (Test-Path (Join-Path $mainDir "$module.py"))) {
        throw "Upstream layout changed: $module.py is missing from $mainDir. Every sync would fail at runtime."
    }
}

# ! Upstream's alass-bin and autosubsync resources are deliberately absent. The plugin runs one
#   engine, and bundling the other two added weight to every download for code never dispatched to.
$bundled = @(
    @{ Source = (Join-Path $mainDir 'VERSION'); Dest = '.' }
)

$pyiArgs = @(
    '-m', 'PyInstaller',
    '--onedir',
    '--noconfirm',
    '--clean',
    '--name', $binaryName,
    '--distpath', $distDir,
    '--workpath', (Join-Path $WorkDir 'pyi-work'),
    '--specpath', (Join-Path $WorkDir 'pyi-spec'),
    '--paths', $mainDir,
    '--paths', $wrapperDir,
    '--runtime-hook', $freezeSupportHook,
    '--additional-hooks-dir', $hooksDir,
    '--hidden-import', 'call_ffsubsync',
    # ! Both are imported inside a function, which PyInstaller's analysis does not follow.
    '--hidden-import', 'cli',
    '--hidden-import', 'assy_vad',
    '--hidden-import', 'webrtcvad',
    '--exclude-module', 'PyQt6.QtWidgets',
    '--exclude-module', 'PyQt6.QtGui',
    '--exclude-module', 'PyQt6.QtQml',
    '--exclude-module', 'PyQt6.QtQuick',
    '--exclude-module', 'tkinter',
    '--exclude-module', 'matplotlib',
    '--exclude-module', 'static_ffmpeg'
)

foreach ($item in $bundled) {
    if (-not (Test-Path $item.Source)) {
        throw "Upstream layout changed: $($item.Source) is missing. The freeze would omit it silently."
    }

    $pyiArgs += '--add-data'
    $pyiArgs += ($item.Source + $dataSeparator + $item.Dest)
}

$pyiArgs += $entryPoint

& $venvPython @pyiArgs | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'PyInstaller failed.' }

$built = Join-Path $distDir $binaryName
if (-not (Test-Path $built)) { throw "PyInstaller produced no output at $built" }

# --- Assertions ------------------------------------------------------------

Write-Host "`n[5/6] Verifying the freeze" -ForegroundColor Cyan

# ! A bundled ffmpeg sets FFMPEG_DIR upstream and overrides the one the plugin supplies.
$stowaways = Get-ChildItem -Path $built -Recurse -File |
    Where-Object { $_.BaseName -in @('ffmpeg', 'ffprobe') }

if ($stowaways) {
    $stowaways | ForEach-Object { Write-Host "  found: $($_.FullName)" -ForegroundColor Red }
    throw 'The freeze bundled ffmpeg/ffprobe. Remove it, or the plugin cannot control which ffmpeg is used.'
}
Write-Host '  no bundled ffmpeg/ffprobe' -ForegroundColor Green

# ! The reverse of the ffmpeg check: alass ships as a native binary, so an --add-data left behind
#   would silently put ~30 MB of unreachable engine back into every download.
$strays = Get-ChildItem -Path $built -Recurse -File |
    Where-Object { $_.Name -like 'alass-cli*' -or $_.Name -like 'alass-linux*' }

if ($strays) {
    $strays | ForEach-Object { Write-Host "  found: $($_.FullName)" -ForegroundColor Red }
    throw 'The freeze bundled the alass binary. The plugin never dispatches to it.'
}
Write-Host '  no bundled alass binary' -ForegroundColor Green

if ($Rid -like 'win-*') {
    $exe = Join-Path $built "$($binaryName).exe"
}
else {
    $exe = Join-Path $built $binaryName
}
if (-not (Test-Path $exe)) { throw "Expected executable not found: $exe" }

# ! Windows-only: spawn re-launches the exe, fork does not. On Linux this argv reaches argparse
#   legitimately and the check would fail on a sound payload.
if ($Rid -like 'win-*') {
    $forkProbe = Invoke-Captured -FilePath $exe `
        -Arguments @('--multiprocessing-fork', 'parent_pid=1', 'pipe_handle=0') -WorkDir $WorkDir

    if ($forkProbe.Output -match 'invalid choice') {
        Write-Host $forkProbe.Output -ForegroundColor Red
        throw 'The freeze does not call multiprocessing.freeze_support(). Every ffsubsync run would hang until killed.'
    }
    Write-Host '  multiprocessing worker argv handled' -ForegroundColor Green
}

if (-not $SkipSmokeTest) {
    $versionRun = Invoke-Captured -FilePath $exe -Arguments @('version') -WorkDir $WorkDir
    if ($versionRun.ExitCode -ne 0) {
        Write-Host $versionRun.Output -ForegroundColor Red
        throw "Smoke test failed: '$($binaryName) version' exited $($versionRun.ExitCode)"
    }
    $reportedVersion = $versionRun.Output.Trim()

    # ! 'Unknown' means main/VERSION never made it in, and other data files will be missing too.
    if ($reportedVersion -match 'unknown' -or $reportedVersion.Length -eq 0) {
        throw "The frozen binary reports its version as '$reportedVersion'. Its data files did not make it into the freeze."
    }
    Write-Host "  version: $reportedVersion" -ForegroundColor Green

    $syncHelp = (Invoke-Captured -FilePath $exe -Arguments @('sync', '--help') -WorkDir $WorkDir).Output
    $missing = @()
    foreach ($engine in $tool.expectedEngines) {
        if ($syncHelp -notmatch [regex]::Escape($engine)) { $missing += $engine }
    }
    if ($missing.Count -gt 0) {
        throw "Frozen binary does not offer these engines: $($missing -join ', '). The plugin's tool chain would fail at runtime."
    }
    Write-Host "  engines present: $($tool.expectedEngines -join ', ')" -ForegroundColor Green

    # ! Being listed in --help proves nothing: two engines were listed while neither could load.
    #   Dispatch happens before the reference is decoded, so a dummy file is enough to make an
    #   engine import its module, and no ffmpeg or real video is needed.
    $probeDir = Join-Path $WorkDir 'engine-probe'
    New-Item -ItemType Directory -Force -Path $probeDir | Out-Null
    $probeSub = Join-Path $probeDir 'probe.srt'
    $probeRef = Join-Path $probeDir 'probe.mkv'
    Write-TextFile -Path $probeSub -Text "1`n00:00:01,000 --> 00:00:02,000`nprobe`n`n"
    Write-TextFile -Path $probeRef -Text "not a video`n"

    # Upstream exits 0 and reports ok even when the engine died on an import, so the exit code
    # cannot be used here. The module-resolution signature is what distinguishes a broken freeze
    # from an engine that merely refused to align a dummy reference.
    $importFailure = "No module named|ModuleNotFoundError|Error while finding module specification"

    foreach ($engine in $tool.expectedEngines) {
        $probeOut = Join-Path $probeDir "out-$engine.srt"
        $probe = Invoke-Captured -FilePath $exe -WorkDir $probeDir -Arguments @(
            '--no-color', 'sync', $probeRef, $probeSub, '-o', $probeOut,
            '-t', $engine, '--json', '--no-prefix')

        if ($probe.Output -match $importFailure) {
            Write-Host $probe.Output -ForegroundColor Red
            throw "The '$engine' engine cannot load its module in the freeze. Every sync using it would fail while assy-cli still reports success."
        }
    }
    Write-Host "  engines load: $($tool.expectedEngines -join ', ')" -ForegroundColor Green

    # ! The audio check's fallback is this subcommand. A freeze that dropped webrtcvad still syncs,
    #   so nothing else here would notice, and every fallback would silently reach no verdict.
    $vadRun = Invoke-Captured -FilePath $exe -Arguments @('vad', '--self-test') -WorkDir $WorkDir
    if ($vadRun.ExitCode -ne 0 -or $vadRun.Output -notmatch '"ok":\s*true') {
        Write-Host $vadRun.Output -ForegroundColor Red
        throw "The frozen binary cannot run 'vad --self-test'. The audio check would have no fallback."
    }
    Write-Host '  vad subcommand answers' -ForegroundColor Green

    # ! Upstream's own subcommands must survive the wrapper.
    $passThrough = (Invoke-Captured -FilePath $exe -Arguments @('--help') -WorkDir $WorkDir).Output
    foreach ($subcommand in @('sync', 'shift', 'batch', 'config', 'version')) {
        if ($passThrough -notmatch [regex]::Escape($subcommand)) {
            throw "The wrapper stopped passing '$subcommand' through to upstream's CLI."
        }
    }
    Write-Host '  upstream subcommands pass through' -ForegroundColor Green
}

# --- Install + record ------------------------------------------------------

Write-Host "`n[6/6] Installing payload and updating the lock" -ForegroundColor Cyan

Remove-Item -Recurse -Force $destRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $destRoot | Out-Null
Copy-Item -Path (Join-Path $built '*') -Destination $destRoot -Recurse -Force

$files = Get-ChildItem -Path $destRoot -Recurse -File
$sizeMb = [math]::Round((($files | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
$treeHash = Get-TreeHash -Path $destRoot


# --- Release archive -------------------------------------------------------

$distRoot = Get-DistRoot
New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

$archiveName = Expand-AssetTemplate -Template $tool.release.assetName -Version $version -Upstream $tool.upstream.version -Tag $tool.upstream.tag -Rid $Rid
$archivePath = Join-Path $distRoot $archiveName

Remove-Item -Force $archivePath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $destRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal

$archiveSha = (Get-FileHash -Path $archivePath -Algorithm SHA256).Hash.ToLower()
$archiveSize = (Get-Item $archivePath).Length

Write-Host "  archive: $archiveName ($([math]::Round($archiveSize / 1MB, 1)) MB)" -ForegroundColor Green

$engineVersions = [ordered]@{}
foreach ($engine in $tool.expectedEngines) {
    if ($resolved.Contains($engine)) { $engineVersions[$engine] = $resolved[$engine] }
    else { $engineVersions[$engine] = 'bundled-by-upstream' }
}

$tool.upstream.tag = $Tag
Set-LockProperty -Target $tool.upstream -Name 'version' -Value ($Tag -replace '^v', '')
$tool.version = $version
Set-LockProperty -Target $tool -Name 'buildPython' -Value $hostSeries

# ! Whichever platform built last wins here. Interpreter and PyInstaller are per-RID under
#   'payloads', which is what Test-PlatformsAgree reads.
$tool.resolved = [ordered]@{
    engines = $engineVersions
    numpy   = $(if ($resolved.Contains('numpy')) { $resolved['numpy'] } else { 'absent' })
    scipy   = $(if ($resolved.Contains('scipy')) { $resolved['scipy'] } else { 'absent' })
}

$payloads = [ordered]@{}
foreach ($property in $tool.payloads.PSObject.Properties) {
    if ($property.Name -ne $Rid) { $payloads[$property.Name] = $property.Value }
}
$payloads[$Rid] = [ordered]@{
    tag           = $Tag
    commit        = $commit
    sha256        = $treeHash
    fileCount     = $files.Count
    sizeMb        = $sizeMb
    archiveName   = $archiveName
    archiveSha256 = $archiveSha
    archiveSize   = $archiveSize
    builtUtc      = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    builtOn       = [System.Environment]::OSVersion.VersionString
    python        = $hostPython.Version
    pyinstaller   = $(if ($resolved.Contains('pyinstaller')) { $resolved['pyinstaller'] } else { 'unknown' })
}
$tool.payloads = $payloads

Write-PayloadLock -Lock $lock
Write-PayloadManifest -Lock $lock

if (-not $KeepWorkDir) {
    Remove-Item -Recurse -Force $venvDir, $distDir, (Join-Path $WorkDir 'pyi-work') -ErrorAction SilentlyContinue
}

Write-Host "`nPayload installed: $destRoot" -ForegroundColor Green
Write-Host "  $($files.Count) files, $sizeMb MB, sha256 $($treeHash.Substring(0,16))..."
Write-Host "Release asset:     $archivePath" -ForegroundColor Green
Write-Host "  sha256 $($archiveSha.Substring(0,16))... now compiled into PayloadManifest.g.cs"

$assetTag = Expand-AssetTemplate -Template $tool.release.assetTag -Version $version
Write-Host "`nUpload it to the '$assetTag' release before publishing the plugin." -ForegroundColor Yellow

# ! Read the keys, not PSObject.Properties. On a hashtable the latter yields Keys/Values/Count.
$builtRids = @($payloads.Keys)
$missingRids = @()
foreach ($required in $tool.requiredRids) {
    if ($builtRids -notcontains $required) { $missingRids += $required }
}
if ($missingRids.Count -gt 0) {
    Write-Host "`nStill missing required platforms: $($missingRids -join ', ')" -ForegroundColor Yellow
    Write-Host 'PyInstaller cannot cross-compile. Run this script on each of those platforms before releasing.' -ForegroundColor Yellow
}
