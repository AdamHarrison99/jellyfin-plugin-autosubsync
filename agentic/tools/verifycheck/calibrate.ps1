# Runs verifycheck against a fixed set of real titles whose behaviour is already known, so a
# change to the audio check can be judged against the same evidence every time.
#
#   .\calibrate.ps1                       # the shipping check only
#   .\calibrate.ps1 -Mode correlate       # plus the envelope-correlation prototype
#   .\calibrate.ps1 -Mode flux            # plus spectral-flux onsets
#   .\calibrate.ps1 -Only 'Twin Peaks'    # one case
#   .\calibrate.ps1 -Vault                # record the current fixtures as the reference
#
# Every case runs twice: as shipped, and with a known 1500ms displacement that the check has to
# hand back. The two controls must not degrade; the unmeasurable ones are the ones under test.
#
# ! The cases live in the media library the plugin itself writes to. A scan that syncs one of
#   them silently replaces the evidence AUDIT.md records, and the next run reads that as a code
#   change - in either direction, inventing a regression or hiding one. fixtures.json records each
#   sidecar's hash so that drift is still reported.
#
# ! The vaulted .srt copies are not in the repo - they are third-party subtitle text. Without them
#   the live sidecar is measured instead and the drift check has nothing to compare. Run -Vault to
#   record local copies; they stay untracked.

[CmdletBinding()]
param(
    [ValidateSet('none', 'correlate', 'flux')]
    [string]$Mode = 'none',
    [string]$Only,
    [int]$Shift = 1500,
    [string]$OutFile,
    [switch]$Vault
)

$ErrorActionPreference = 'Continue'
$project = $PSScriptRoot
$vaultDir = Join-Path $PSScriptRoot 'fixtures'
$lockPath = Join-Path $vaultDir 'fixtures.json'

$casePaths = Join-Path $PSScriptRoot 'calibrate.local.json'

# ! The five titles live in a real media library, so their paths are machine-local and are ¬kept
#   in the repo. calibrate.local.json is untracked: one { Id, Video, Subtitle } object per case.
#   Without it the harness still runs against the vaulted fixtures for anything it can reach.
$paths = @{}
if (Test-Path -LiteralPath $casePaths) {
    foreach ($entry in (Get-Content -LiteralPath $casePaths -Raw | ConvertFrom-Json)) {
        $paths[$entry.Id] = $entry
    }
}
else {
    Write-Host "! $casePaths is missing - no case has a video path. Create it to run the set."
}

$cases = @(
    @{ Id = 'madmen-s02e06';   Name = 'Mad Men S02E06 — unmeasurable, mean -26dB' }
    @{ Id = 'mpfc-s01e02';     Name = 'MPFC S01E02 — unmeasurable, continuous score' }
    @{ Id = 'simpsons-s01e10'; Name = 'The Simpsons S01E10 — unmeasurable, laugh bed' }
    @{ Id = 'tng-s02e02';      Name = 'TNG S02E02 — control, measurable, genuinely 1400ms off' }
    @{ Id = 'twinpeaks-fwwm';  Name = 'Twin Peaks FWWM — control, the strongest title available' }
) | ForEach-Object {
    $entry = $paths[$_.Id]
    $_.Video = if ($entry) { $entry.Video } else { $null }
    $_.Subtitle = if ($entry) { $entry.Subtitle } else { $null }
    $_
}

$selected = if ($Only) { $cases | Where-Object { $_.Name -like "*$Only*" } } else { $cases }

if (-not $selected) {
    Write-Error "No case matches '$Only'"
    exit 2
}

function Get-Sha([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# ! Identity for a multi-gigabyte file over SMB. Hashing the whole thing costs minutes per title;
#   a re-encode changes the length and the opening megabyte together.
function Get-VideoIdentity([string]$path) {
    $item = Get-Item -LiteralPath $path
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $buffer = New-Object byte[] (1MB)
        $read = $stream.Read($buffer, 0, $buffer.Length)
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try {
            $head = [BitConverter]::ToString($sha.ComputeHash($buffer, 0, $read)).Replace('-', '').ToLowerInvariant()
        }
        finally { $sha.Dispose() }
    }
    finally { $stream.Dispose() }

    return [pscustomobject]@{ Bytes = $item.Length; Head = $head }
}

function Read-Lock {
    if (-not (Test-Path -LiteralPath $lockPath)) { return @{} }
    $table = @{}
    foreach ($entry in (Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json)) {
        $table[$entry.Id] = $entry
    }
    return $table
}

if ($Vault) {
    if (-not (Test-Path -LiteralPath $vaultDir)) {
        New-Item -ItemType Directory -Path $vaultDir -Force | Out-Null
    }

    # ! Merged, ¬replaced. -Vault w/ -Only must not drop the cases it did not look at.
    $lock = Read-Lock

    foreach ($case in $selected) {
        if (-not $case.Subtitle -or -not (Test-Path -LiteralPath $case.Subtitle)) {
            Write-Host "  skip    $($case.Id): subtitle not reachable from here"
            continue
        }

        $vaultFile = "$($case.Id).srt"
        Copy-Item -LiteralPath $case.Subtitle -Destination (Join-Path $vaultDir $vaultFile) -Force

        $video = $null
        if ($case.Video -and (Test-Path -LiteralPath $case.Video)) { $video = Get-VideoIdentity $case.Video }

        $lock[$case.Id] = [pscustomobject]@{
            Id = $case.Id
            Name = $case.Name
            SubtitleSha256 = Get-Sha $case.Subtitle
            VideoBytes = if ($video) { $video.Bytes } else { 0 }
            VideoHead = if ($video) { $video.Head } else { '' }
            RecordedUtc = (Get-Date).ToUniversalTime().ToString('o')
            VaultFile = $vaultFile
        }

        Write-Host "  vaulted $($case.Id)"
    }

    $lock.Values | Sort-Object Id | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $lockPath -Encoding utf8
    Write-Host "`nRecorded $($lock.Count) fixtures in $lockPath"
    exit 0
}

$lock = Read-Lock
$lines = New-Object System.Collections.Generic.List[string]
$drifted = 0

function Emit([string]$text) {
    $lines.Add($text)
    Write-Host $text
}

foreach ($case in $selected) {
    Emit ''
    Emit "=== $($case.Name) ==="

    if (-not $case.Video -or -not (Test-Path -LiteralPath $case.Video)) {
        Emit '  video not reachable from here'
        continue
    }

    $fixture = $lock[$case.Id]
    $subtitle = $case.Subtitle

    if (-not $fixture) {
        Emit '  ! no vaulted fixture — measuring the live sidecar. Run -Vault to record one.'
    }
    else {
        $vaulted = Join-Path $vaultDir $fixture.VaultFile

        if (-not (Test-Path -LiteralPath $vaulted)) {
            Emit "  ! fixture $($fixture.VaultFile) is missing — measuring the live sidecar."
        }
        else {
            $subtitle = $vaulted

            # The sidecar is the file the plugin rewrites, so it is the one that drifts.
            if ($case.Subtitle -and (Test-Path -LiteralPath $case.Subtitle) -and (Get-Sha $case.Subtitle) -ne $fixture.SubtitleSha256) {
                $drifted++
                $when = (Get-Item -LiteralPath $case.Subtitle).LastWriteTime
                Emit "  ! DRIFT: the live sidecar differs from the recorded fixture (written $when)."
                Emit "    Measuring the vaulted copy. AUDIT.md describes that copy, ¬the live file."
            }

            # A replaced release changes the audio under the same cues, and no vault can restore it.
            $video = Get-VideoIdentity $case.Video
            if ($fixture.VideoBytes -ne 0 -and
                ($video.Bytes -ne $fixture.VideoBytes -or $video.Head -ne $fixture.VideoHead)) {
                $drifted++
                Emit '  ! DRIFT: the video is not the one this fixture was recorded against.'
                Emit '    The recorded behaviour is stale — re-measure deliberately and update AUDIT.md.'
            }
        }
    }

    $extra = @()
    if ($Mode -ne 'none') { $extra += "--$Mode" }

    $output = & dotnet run --project $project --nologo -v quiet -- `
        --video $case.Video --subtitle $subtitle --shift $Shift @extra

    foreach ($line in $output) { Emit $line }
}

if ($drifted -gt 0) {
    Emit ''
    Emit "! $drifted fixture check(s) drifted. A verdict change here is a file change before it is a code change."
}

if ($OutFile) {
    $lines | Out-File -FilePath $OutFile -Encoding utf8
    Write-Host "`nWritten to $OutFile"
}
