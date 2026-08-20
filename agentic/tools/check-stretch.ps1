<#
.SYNOPSIS
What rate change the engine actually applied to each title the stretch guard refused.

.DESCRIPTION
The guard at SyncOrchestrator refuses any span change over AlignedWithinMs when the audio check
did not measure drift, and logs the millisecond figure alone. A millisecond figure cannot be judged:
1261 ms is a defect on a two-minute clip and the exact NTSC pulldown ratio on a 21-minute episode.

This reads the source sidecar's own cue span and converts the logged figure into the ratio the
engine applied, then names the framerate conversion that ratio matches. Sidecars only; no video is
opened, so this is cheap against a network library.

! The Conversion column is NOT evidence that the engine was right. ffsubsync picks its rate from a
fixed list of standard framerate ratios, so every output it produces lands on one -- including
output from a subtitle belonging to a different show. Verified with scorecheck's mismatched-pair
baseline. Read this column as the magnitude of the change, never as its correctness.

! A field log names sidecars that later runs may already have rewritten. Confirm a title was never
written before treating its span as the one the refusal was about.

.EXAMPLE
.\agentic\tools\check-stretch.ps1 -Log \\<server>\Jellyfin\Server\log\log_20260816.log

.EXAMPLE
.\agentic\tools\check-stretch.ps1 -Log a.log,b.log -Csv .\stretch.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Log,
    [int]$Take = 0,
    [string]$Csv
)

$ErrorActionPreference = 'Stop'

# The conversions in circulation. A subtitle authored against one release and shipped with another
# lands on one of these; anything else is the engine guessing.
$known = @(
    @{ Name = '23.976 -> 24'; Ratio = 1000 / 999 }
    @{ Name = '24 -> 23.976'; Ratio = 999 / 1000 }
    @{ Name = '25 -> 23.976'; Ratio = 25 / 23.976 }
    @{ Name = '23.976 -> 25'; Ratio = 23.976 / 25 }
    @{ Name = '25 -> 24'; Ratio = 25 / 24 }
    @{ Name = '24 -> 25'; Ratio = 24 / 25 }
    @{ Name = '30 -> 23.976'; Ratio = 30 / 23.976 }
    @{ Name = '23.976 -> 30'; Ratio = 23.976 / 30 }
)

# ! Loose enough to absorb the engine's own fit error, tight enough that the nearest named
#   conversion stays a claim and not a coincidence. Neighbouring ratios are 4% apart.
$tolerance = 0.004

# The guard's own threshold, read out of the shipping source so this cannot drift from it.
$root = Split-Path (Split-Path (Split-Path $PSCommandPath -Parent) -Parent) -Parent
$verifier = Get-Content (Join-Path $root 'Services\SyncVerifier.cs') -Raw
if ($verifier -notmatch 'AlignedWithinMs\s*=\s*(\d+)') { throw 'cannot read AlignedWithinMs from SyncVerifier.cs' }
$alignedWithin = [int]$Matches[1]
if ($verifier -notmatch 'DriftWindows\s*=\s*(\d+)') { throw 'cannot read DriftWindows from SyncVerifier.cs' }
$driftWindows = [int]$Matches[1]
Write-Host "guard: AlignedWithinMs=$alignedWithin  DriftWindows=$driftWindows`n"

# Pair each refusal with the sidecar it refused. The rejection line carries only the file name; the
# assy-cli invocation earlier in the same log carries the full path.
$work = @()
foreach ($path in $Log) {
    $lines = Get-Content $path
    $refused = @{}

    foreach ($line in $lines) {
        if ($line -match '\("ext:(?<k>[^"]+)"\): it stretches the subtitle by (?<ms>-?\d+) ms.+\((?<w>\d+) windows') {
            $refused[$Matches['k']] = @([int]$Matches['ms'], [int]$Matches['w'])
        }
    }

    foreach ($line in $lines) {
        if ($line -match 'sync (?<v>[A-Za-z]:\\.+?) (?<s>[A-Za-z]:\\.+?\.(srt|ass|ssa|vtt)) -o ') {
            $leaf = Split-Path $Matches['s'] -Leaf
            if ($refused.ContainsKey($leaf)) {
                $work += [pscustomobject]@{
                    Sub = $Matches['s']
                    Ms  = $refused[$leaf][0]
                    Win = $refused[$leaf][1]
                }
            }
        }
    }
}

$work = $work | Sort-Object Sub -Unique
if ($Take -gt 0) { $work = $work | Select-Object -First $Take }
Write-Host "measuring $($work.Count) refused titles`n"

# First and last cue start, in milliseconds. Both cue grammars, since .ass is in the population.
function Get-Span([string]$file) {
    $text = Get-Content -LiteralPath $file -Raw -ErrorAction Stop
    $stamps = @()

    foreach ($m in [regex]::Matches($text, '(?<h>\d+):(?<m>\d{2}):(?<s>\d{2})[,.](?<f>\d{2,3})')) {
        $frac = $m.Groups['f'].Value
        # .ass writes centiseconds where .srt writes milliseconds.
        $ms = if ($frac.Length -eq 2) { [int]$frac * 10 } else { [int]$frac }
        $stamps += ([int]$m.Groups['h'].Value * 3600000) + ([int]$m.Groups['m'].Value * 60000) +
                   ([int]$m.Groups['s'].Value * 1000) + $ms
    }

    if ($stamps.Count -lt 2) { return $null }
    $sorted = $stamps | Sort-Object
    return $sorted[-1] - $sorted[0]
}

$results = @()
$n = 0

foreach ($item in $work) {
    $n++

    # ! -LiteralPath throughout. Release folders named "[BRSHNKV]" are wildcard character classes.
    if (-not (Test-Path -LiteralPath $item.Sub)) {
        Write-Host ("[{0,3}/{1}] missing sidecar" -f $n, $work.Count)
        continue
    }

    $span = Get-Span $item.Sub
    if ($null -eq $span -or $span -le 0) {
        Write-Host ("[{0,3}/{1}] unreadable cue span" -f $n, $work.Count)
        continue
    }

    $ratio = 1 + ($item.Ms / $span)

    $match = 'none'
    foreach ($k in $known) {
        if ([Math]::Abs($ratio - $k.Ratio) -le $tolerance) { $match = $k.Name; break }
    }

    # Whether the title is even long enough for the check to plan DriftWindows windows, which is
    # what the guard demands before it will admit a stretch.
    $canMeasure = $item.Win -ge $driftWindows

    $results += [pscustomobject]@{
        Title      = Split-Path $item.Sub -Leaf
        Ms         = $item.Ms
        SpanMin    = [Math]::Round($span / 60000, 1)
        Ratio      = [Math]::Round($ratio, 5)
        Conversion = $match
        Win        = $item.Win
        CanMeasure = $canMeasure
    }

    Write-Host ("[{0,3}/{1}] {2,8} ms over {3,5:F1} min  ratio {4,7:F5}  {5,-14} {6,2} win  {7}" -f `
        $n, $work.Count, $item.Ms, ($span / 60000), $ratio, $match, $item.Win, $results[-1].Title)
}

Write-Host "`n--- what the engine applied ---"
$results | Group-Object Conversion | Sort-Object Count -Descending |
    Format-Table @{L = 'Count'; E = { $_.Count } }, @{L = 'Conversion'; E = { $_.Name } } -AutoSize

Write-Host "--- could the check have measured drift at all? ---"
$results | Group-Object CanMeasure | Sort-Object Count -Descending |
    Format-Table @{L = 'Count'; E = { $_.Count } },
                 @{L = "Title reached $driftWindows windows"; E = { $_.Name } } -AutoSize

Write-Host "--- named conversions the guard refused ---"
$results | Where-Object { $_.Conversion -ne 'none' } | Group-Object Conversion, CanMeasure |
    Format-Table @{L = 'Count'; E = { $_.Count } }, @{L = 'Conversion / measurable'; E = { $_.Name } } -AutoSize

if ($Csv) {
    $results | Export-Csv $Csv -NoTypeInformation
    Write-Host "wrote $Csv"
}
