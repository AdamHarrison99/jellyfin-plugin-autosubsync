<#
.SYNOPSIS
Which gate refused each title the audio check could not measure.

.DESCRIPTION
`Nothing()` discards the strength it measured, so an inconclusive verdict reaches the log as
"peak 0.00x" whichever of BestShift's three gates actually fired. This runs verifycheck --profile
over a set of titles and reports the gate, so a coverage loss can be attributed instead of guessed.

Pairs come from a Jellyfin log: every assy-cli invocation carries the full video and subtitle path,
and the verify line that follows carries the verdict.

.EXAMPLE
.\agentic\tools\check-inconclusive.ps1 -Log \\<server>\Jellyfin\Server\log\log_20260815.log -From 74626

.EXAMPLE
.\agentic\tools\check-inconclusive.ps1 -Pairs .\pairs.csv -Take 10
#>
[CmdletBinding(DefaultParameterSetName = 'Log')]
param(
    [Parameter(ParameterSetName = 'Log', Mandatory)][string]$Log,
    [Parameter(ParameterSetName = 'Log')][int]$From = 0,
    [Parameter(ParameterSetName = 'Pairs', Mandatory)][string]$Pairs,
    [int]$Take = 0,
    [string]$Csv
)

$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path (Split-Path $PSCommandPath -Parent) -Parent) -Parent

# The gate constants, read out of the shipping source so this cannot drift from what runs.
$source = Get-Content (Join-Path $root 'Services\SyncVerifier.cs') -Raw
function Constant([string]$name, [string]$pattern = '[\d.]+') {
    if ($source -notmatch "$name\s*=\s*($pattern)") { throw "cannot read $name from SyncVerifier.cs" }
    return [double]$Matches[1]
}

$minimumHits = Constant 'MinimumHits'
$minimumHitShare = Constant 'MinimumHitShare'
$peakRatio = Constant 'PeakRatio'
$rivalRatio = Constant 'RivalRatio'
Write-Host "gates: MinimumHits=$minimumHits MinimumHitShare=$minimumHitShare PeakRatio=$peakRatio RivalRatio=$rivalRatio`n"

if ($PSCmdlet.ParameterSetName -eq 'Log') {
    $lines = Get-Content $Log
    if ($From -gt 0) { $lines = $lines[$From..($lines.Count - 1)] }

    # Names the check returned no verdict on. Its strength prints as 0.00 on every such path.
    $unmeasured = @{}
    foreach ($line in $lines) {
        if ($line -match 'peak 0\.00x' -and $line -match '\("ext:(?<f>[^"]+)"\)') { $unmeasured[$Matches['f']] = $true }
    }

    $work = @()
    foreach ($line in $lines) {
        if ($line -match 'sync (?<v>[A-Za-z]:\\.+?) (?<s>[A-Za-z]:\\.+?\.(srt|ass|ssa|vtt)) -o ') {
            if ($unmeasured.ContainsKey((Split-Path $Matches['s'] -Leaf))) {
                $work += [pscustomobject]@{ Video = $Matches['v']; Sub = $Matches['s'] }
            }
        }
    }
    $work = $work | Sort-Object Sub -Unique
}
else {
    $work = Import-Csv $Pairs
}

if ($Take -gt 0) { $work = $work | Select-Object -First $Take }
Write-Host "profiling $($work.Count) titles`n"

# One build, then the executable directly; dotnet run would rebuild once per title.
& dotnet build (Join-Path $root 'agentic\tools\verifycheck') -c Debug --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'verifycheck failed to build' }
$exe = Join-Path $root 'agentic\tools\verifycheck\bin\Debug\net9.0\verifycheck.exe'

$results = @()
$n = 0

foreach ($pair in $work) {
    $n++
    # ! -LiteralPath. A release folder named "[UTR]" is a wildcard character class otherwise, and
    #   the title reads as missing media.
    if (-not (Test-Path -LiteralPath $pair.Video) -or -not (Test-Path -LiteralPath $pair.Sub)) {
        Write-Host ("[{0,3}/{1}] missing media, skipped" -f $n, $work.Count)
        continue
    }

    # ! The shipping result now carries the hit count, the floor and the onset supply it actually
    #   judged. Recomputing them here is how a harness drifts from the code it checks.
    $text = & $exe --video $pair.Video --subtitle $pair.Sub 2>&1 | Out-String

    if ($text -notmatch '(?<verdict>Aligned|Misaligned|Inconclusive)\s.+?peak\s+(?<peak>[\d.]+)x\s+(?<hits>\d+) hits /\s+(?<floor>\d+) floor\s+(?<onsets>\d+) onsets\s+(?<win>\d+) windows') { continue }

    $verdict = $Matches['verdict']
    $strength = [double]$Matches['peak']
    $hits = [int]$Matches['hits']
    $floor = [int]$Matches['floor']
    $onsets = [int]$Matches['onsets']
    $windows = [int]$Matches['win']
    $at = if ($text -match '(?:Aligned|Misaligned)\s+(?<at>-?\d+)ms') { [int]$Matches['at'] } else { $null }

    # Which gate refused it. The mean test is the one left when the other two passed.
    $gate = if ($verdict -ne 'Inconclusive') { 'measures' }
    elseif ($hits -lt $floor -and $strength -lt $rivalRatio) { 'both' }
    elseif ($hits -lt $floor) { 'floor' }
    elseif ($strength -lt $rivalRatio) { 'rival' }
    else { 'mean' }

    $results += [pscustomobject]@{
        Title    = Split-Path $pair.Sub -Leaf
        Verdict  = $verdict
        Windows  = $windows
        Onsets   = $onsets
        Hits     = $hits
        Floor    = $floor
        AtMs     = $at
        Strength = $strength
        # What share of the onsets available were hit, which is what the title can actually supply.
        OnsetHit = if ($onsets -gt 0) { [math]::Round($hits / $onsets, 2) } else { 0 }
        Gate     = $gate
    }

    Write-Host ("[{0,3}/{1}] {2,-8} {3,-12} {4,4} hits /{5,4} floor {6,5} onsets  peak {7,4:F2}x  {8}" -f `
        $n, $work.Count, $gate, $verdict, $hits, $floor, $onsets, $strength, $results[-1].Title)
}

Write-Host "`n--- population split ---"
$results | Group-Object Gate | Sort-Object Count -Descending |
    Format-Table @{L = 'Count'; E = { $_.Count } }, @{L = 'Refused by'; E = { $_.Name } } -AutoSize

Write-Host "--- titles the floor alone refused ---"
$results | Where-Object Gate -eq 'floor' | Format-Table `
    @{L = 'Onsets'; E = { $_.Onsets } },
    @{L = 'Hits'; E = { $_.Hits } },
    @{L = 'Floor'; E = { $_.Floor } },
    @{L = 'peak'; E = { '{0:F2}x' -f $_.Strength } },
    @{L = 'onset hit'; E = { '{0:P0}' -f $_.OnsetHit } },
    # The full name goes to the CSV; the console only has to identify the row.
    @{L = 'Title'; E = { if ($_.Title.Length -gt 46) { $_.Title.Substring(0, 46) } else { $_.Title } } }

if ($Csv) {
    $results | Export-Csv $Csv -NoTypeInformation
    Write-Host "wrote $Csv"
}
