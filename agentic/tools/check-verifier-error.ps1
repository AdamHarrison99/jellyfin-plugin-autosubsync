# How far the audio check's reading sits from the truth, over real titles.
#
#   .\agentic\tools\check-verifier-error.ps1 -Video <path> [-Video <path> ...]
#   .\agentic\tools\check-verifier-error.ps1 -Show "<library>\TV Shows\<name>" -Take 8
#
# The audio check measures a cue against the speech it belongs to. check-vs-embedded measures the
# same cue against the video's own embedded track. The gap between the two is what AlignedWithinMs
# has to absorb, and it is the number that decides whether a verdict near the bound can be trusted.
#
# Needs an embedded subtitle track to compare against; a title without one is reported and skipped.

[CmdletBinding()]
param(
    [string[]]$Video = @(),
    [string]$Show,
    [int]$Take = 8,
    [string]$OutFile
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$verifycheck = Join-Path $PSScriptRoot 'verifycheck'
$embedded = Join-Path $PSScriptRoot 'check-vs-embedded.ps1'

if ($Show) {
    $Video += Get-ChildItem -LiteralPath $Show -File -Recurse -Depth 3 -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.mkv', '.mp4' } |
        Select-Object -First $Take -ExpandProperty FullName
}

if (-not $Video) { Write-Error 'No videos given; pass -Video or -Show'; exit 2 }

$lines = New-Object System.Collections.Generic.List[string]
function Emit([string]$text) { $lines.Add($text); Write-Host $text }

$gaps = New-Object System.Collections.Generic.List[double]

Emit ('{0,-46} {1,10} {2,10} {3,8}' -f 'title', 'check', 'truth', 'gap')
Emit ('-' * 78)

foreach ($path in $Video) {
    $name = [IO.Path]::GetFileNameWithoutExtension($path)
    $dir = Split-Path -Parent $path

    $subtitle = Get-ChildItem -LiteralPath $dir -Filter "$name*.srt" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notmatch '\.forced\.' } | Select-Object -First 1

    if (-not $subtitle) { continue }

    # The shipping check, as the plugin would run it.
    $checkOut = & dotnet run --project $verifycheck --nologo -v quiet -- --video $path --subtitle $subtitle.FullName
    $checkLine = $checkOut | Where-Object { $_ -match 'as shipped' } | Select-Object -First 1

    $check = $null
    if ($checkLine -match '^(Aligned|Misaligned)\s+(-?\d+)ms') { $check = [int]$Matches[2] }
    $verdict = if ($checkLine -match '^(\w+)') { $Matches[1] } else { '?' }

    # Ground truth, independent of both the engine and the audio.
    $truthOut = & $embedded -Video $path -Subtitle $subtitle.FullName 2>&1
    $truth = $null
    foreach ($line in $truthOut) {
        if ($line -match 'VERDICT\s*:\s*OUT by ([+-]?\d+) ms') { $truth = [int]$Matches[1] }
        elseif ($line -match 'VERDICT\s*:\s*IN SYNC') { $truth = 0 }
    }

    $short = $name.Substring(0, [Math]::Min(46, $name.Length))

    if ($null -eq $truth) { Emit ('{0,-46} {1,10} {2,10}' -f $short, $verdict, 'no truth'); continue }
    if ($null -eq $check) { Emit ('{0,-46} {1,10} {2,10}' -f $short, $verdict, "$truth ms"); continue }

    $gap = $check - $truth
    $gaps.Add([double]$gap)
    Emit ('{0,-46} {1,10} {2,10} {3,8}' -f $short, "$check ms", "$truth ms", "$gap ms")
}

if ($gaps.Count -gt 0) {
    $sorted = $gaps | Sort-Object
    $median = $sorted[[int]($sorted.Count / 2)]
    $absMax = ($gaps | ForEach-Object { [Math]::Abs($_) } | Measure-Object -Maximum).Maximum
    Emit ''
    Emit ("{0} measured   median gap {1:F0} ms   spread {2:F0} to {3:F0} ms   worst |gap| {4:F0} ms" -f `
        $gaps.Count, $median, ($sorted | Select-Object -First 1), ($sorted | Select-Object -Last 1), $absMax)
}

if ($OutFile) { $lines | Out-File -FilePath $OutFile -Encoding utf8; Write-Host "`nWritten to $OutFile" }
