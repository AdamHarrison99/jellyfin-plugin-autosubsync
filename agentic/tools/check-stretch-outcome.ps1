<#
.SYNOPSIS
What the stretch guard actually threw away: sync each refused title for real and score the result.

.DESCRIPTION
check-stretch.ps1 shows that every refused stretch was a standard framerate conversion, but a
standard ratio says nothing about the constant offset applied alongside it, and the plugin deletes
the produced file at SyncOrchestrator :395 so the field log cannot answer it either.

This reproduces the refused run end to end and reports three things per title: the audio check's
verdict on the original, the engine's own declared scale factor and score, and the audio check's
verdict on what the engine produced. The last column is the one that decides whether a
named-conversion allowance would admit good files or bad ones.

! Reads whole video audio twice per title through ffsubsync and the check. Keep the sample small
against a network library.

.EXAMPLE
.\agentic\tools\check-stretch-outcome.ps1 -Pairs .\sample.csv

.EXAMPLE
.\agentic\tools\check-stretch-outcome.ps1 -Log \\<server>\Jellyfin\Server\log\log_20260816.log -Take 6
#>
[CmdletBinding(DefaultParameterSetName = 'Pairs')]
param(
    [Parameter(ParameterSetName = 'Pairs', Mandatory)][string]$Pairs,
    [Parameter(ParameterSetName = 'Log', Mandatory)][string[]]$Log,
    [int]$Take = 0,
    [string]$Exe,
    [string]$Csv
)

# ! Continue, as scorecheck does. Under PS 5.1 redirecting a native exe's stderr wraps every line
#   in an ErrorRecord, and ffsubsync writes its whole progress log there. Explicit throws still fire.
$ErrorActionPreference = 'Continue'
$root = Split-Path (Split-Path (Split-Path $PSCommandPath -Parent) -Parent) -Parent

if (-not $Exe) { $Exe = Join-Path $root 'agentic\payload\assy-cli\win-x64\assy-cli.exe' }
if (-not (Test-Path -LiteralPath $Exe)) { throw "assy-cli not found at $Exe. Run build-assy.ps1, or pass -Exe." }

if ($PSCmdlet.ParameterSetName -eq 'Log') {
    $work = @()
    foreach ($path in $Log) {
        $lines = Get-Content $path
        $refused = @{}

        foreach ($line in $lines) {
            if ($line -match '\("ext:(?<k>[^"]+)"\): it stretches the subtitle by (?<ms>-?\d+) ms') {
                $refused[$Matches['k']] = [int]$Matches['ms']
            }
        }

        foreach ($line in $lines) {
            if ($line -match 'sync (?<v>[A-Za-z]:\\.+?) (?<s>[A-Za-z]:\\.+?\.(srt|ass|ssa|vtt)) -o ') {
                $leaf = Split-Path $Matches['s'] -Leaf
                if ($refused.ContainsKey($leaf)) {
                    $work += [pscustomobject]@{ Video = $Matches['v']; Sub = $Matches['s']; Ms = $refused[$leaf] }
                }
            }
        }
    }
    $work = $work | Sort-Object Sub -Unique
}
else {
    $work = Import-Csv $Pairs
}

if ($Take -gt 0) { $work = $work | Select-Object -First $Take }
Write-Host "running $($work.Count) refused titles end to end`n"

& dotnet build (Join-Path $root 'agentic\tools\verifycheck') -c Debug --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'verifycheck failed to build' }
$check = Join-Path $root 'agentic\tools\verifycheck\bin\Debug\net9.0\verifycheck.exe'

# The verdict line the harness prints, reduced to the fields worth comparing across two runs.
function Read-Verdict([string]$video, [string]$subtitle) {
    $text = & $check --video $video --subtitle $subtitle 2>&1 | Out-String

    if ($text -match '(?<v>Aligned|Misaligned|Inconclusive)(\s+(?<at>-?\d+)ms)?.+?peak\s+(?<p>[\d.]+)x\s+(?<h>\d+) hits /\s+(?<f>\d+) floor\s+(?<o>\d+) onsets\s+(?<w>\d+) windows') {
        return [pscustomobject]@{
            Verdict = $Matches['v']
            AtMs    = if ($Matches['at']) { [int]$Matches['at'] } else { $null }
            Peak    = [double]$Matches['p']
            Hits    = [int]$Matches['h']
            Floor   = [int]$Matches['f']
            Windows = [int]$Matches['w']
        }
    }

    return $null
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('stretch-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$results = @()
$n = 0

try {
    foreach ($item in $work) {
        $n++
        $name = Split-Path $item.Sub -Leaf
        Write-Host ("[{0}/{1}] {2}" -f $n, $work.Count, $name)

        if (-not (Test-Path -LiteralPath $item.Video) -or -not (Test-Path -LiteralPath $item.Sub)) {
            Write-Host '        missing media'
            continue
        }

        $before = Read-Verdict $item.Video $item.Sub
        Write-Host ("        before  {0}" -f $(if ($before) { "$($before.Verdict) $($before.AtMs)ms  peak $('{0:F2}' -f $before.Peak)x  $($before.Windows) win" } else { 'no reading' }))

        $out = Join-Path $scratch ((New-Guid).ToString('N') + [IO.Path]::GetExtension($item.Sub))
        $err = Join-Path $scratch ((New-Guid).ToString('N') + '.err')

        $clock = [Diagnostics.Stopwatch]::StartNew()
        & $Exe sync $item.Video $item.Sub -o $out --json --encoding same_as_input 2>$err | Out-Null
        $clock.Stop()

        $text = if (Test-Path -LiteralPath $err) { Get-Content -LiteralPath $err -Raw } else { '' }
        $scale = [regex]::Match($text, 'framerate scale factor:\s*(-?[\d.]+)')
        $shift = [regex]::Match($text, 'offset seconds:\s*(-?[\d.]+)')
        $score = [regex]::Match($text, 'score:\s*(-?[\d.]+)')

        Write-Host ("        engine  scale {0}  offset {1}s  score {2}  in {3:N0}s" -f `
            $(if ($scale.Success) { $scale.Groups[1].Value } else { '-' }),
            $(if ($shift.Success) { $shift.Groups[1].Value } else { '-' }),
            $(if ($score.Success) { $score.Groups[1].Value } else { '-' }),
            $clock.Elapsed.TotalSeconds)

        if (-not (Test-Path -LiteralPath $out)) {
            Write-Host '        engine produced nothing'
            continue
        }

        $after = Read-Verdict $item.Video $out
        Write-Host ("        after   {0}`n" -f $(if ($after) { "$($after.Verdict) $($after.AtMs)ms  peak $('{0:F2}' -f $after.Peak)x  $($after.Hits)/$($after.Floor) hits" } else { 'no reading' }))

        $results += [pscustomobject]@{
            Title       = $name
            Before      = if ($before) { $before.Verdict } else { 'none' }
            BeforeAtMs  = if ($before) { $before.AtMs } else { $null }
            BeforePeak  = if ($before) { $before.Peak } else { $null }
            Scale       = if ($scale.Success) { [double]$scale.Groups[1].Value } else { $null }
            OffsetS     = if ($shift.Success) { [double]$shift.Groups[1].Value } else { $null }
            Score       = if ($score.Success) { [double]$score.Groups[1].Value } else { $null }
            After       = if ($after) { $after.Verdict } else { 'none' }
            AfterAtMs   = if ($after) { $after.AtMs } else { $null }
            AfterPeak   = if ($after) { $after.Peak } else { $null }
            Windows     = if ($after) { $after.Windows } else { $null }
        }
    }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`n--- what the guard threw away ---"
$results | Format-Table `
    @{L = 'before'; E = { $_.Before } },
    @{L = 'scale'; E = { if ($null -ne $_.Scale) { '{0:F5}' -f $_.Scale } else { '-' } } },
    @{L = 'offset s'; E = { $_.OffsetS } },
    @{L = 'after'; E = { $_.After } },
    @{L = 'at'; E = { if ($null -ne $_.AfterAtMs) { "$($_.AfterAtMs)ms" } else { '-' } } },
    @{L = 'peak'; E = { if ($null -ne $_.AfterPeak) { '{0:F2}x' -f $_.AfterPeak } else { '-' } } },
    @{L = 'win'; E = { $_.Windows } },
    @{L = 'Title'; E = { if ($_.Title.Length -gt 44) { $_.Title.Substring(0, 44) } else { $_.Title } } } -AutoSize

Write-Host "--- verdict on the engine's own output ---"
$results | Group-Object After | Sort-Object Count -Descending |
    Format-Table @{L = 'Count'; E = { $_.Count } }, @{L = 'After'; E = { $_.Name } } -AutoSize

if ($Csv) {
    $results | Export-Csv $Csv -NoTypeInformation
    Write-Host "wrote $Csv"
}
