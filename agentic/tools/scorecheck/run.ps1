# Reads the alignment score the engine already prints, and asks whether it separates a real
# alignment from a false one.
#
# ffsubsync scores an alignment as (reference speech frames matched with subtitle speech frames)
# minus (reference speech frames matched with subtitle silence), over 10ms frames, and prints it
# on stderr as `score:`. It is computed on ffsubsync's own VAD rather than on a level threshold,
# which is why it can carry information about titles the plugin's audio check cannot measure at
# all. The magnitude is not normalized upstream, so this reports it per second of displayed
# subtitle as well as raw.
#
#   .\run.ps1 -Video x.mkv -Subtitle x.eng.srt                    # the honest pairing
#   .\run.ps1 -Video x.mkv -Subtitle other-episode.eng.srt        # a pairing that cannot align
#
# A subtitle from a different episode is the calibration baseline: the engine still returns an
# offset and still claims success, so whatever separates that case from a real one is the only
# thing a quality gate could ever key on.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Video,
    [Parameter(Mandatory = $true)][string[]]$Subtitle,
    [string]$Exe,
    [string]$Label
)

$ErrorActionPreference = 'Continue'

if (-not $Exe) {
    $Exe = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'payload\assy-cli\win-x64\assy-cli.exe'
}

if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Error "assy-cli not found at $Exe. Run build-assy.ps1, or pass -Exe."
    exit 2
}

# Seconds of subtitle actually on screen. The score grows with this, so it is the divisor that
# makes two titles comparable at all.
function Get-DisplayedSeconds([string]$path) {
    $total = 0.0
    $pattern = '(\d{1,2}):(\d{2}):(\d{2})[,.](\d{2,3})\s*-->\s*(\d{1,2}):(\d{2}):(\d{2})[,.](\d{2,3})'

    foreach ($line in [System.IO.File]::ReadLines($path)) {
        $m = [regex]::Match($line, $pattern)
        if (-not $m.Success) { continue }

        $at = @(1, 5) | ForEach-Object {
            $g = $_
            $frac = $m.Groups[$g + 3].Value
            $ms = if ($frac.Length -eq 2) { [int]$frac * 10 } else { [int]$frac }
            ((([int]$m.Groups[$g].Value * 60) + [int]$m.Groups[$g + 1].Value) * 60 + [int]$m.Groups[$g + 2].Value) * 1000 + $ms
        }

        $span = $at[1] - $at[0]
        if ($span -gt 0) { $total += $span / 1000.0 }
    }

    return $total
}

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("scorecheck-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null

Write-Host ("{0,-46} {1,10} {2,9} {3,10} {4,8} {5,7}" -f 'subtitle', 'score', 'shown s', 'per second', 'offset', 'rate')
Write-Host ('-' * 96)

try {
    foreach ($path in $Subtitle) {
        if (-not (Test-Path -LiteralPath $path)) {
            Write-Host ("{0,-46} {1}" -f (Split-Path -Leaf $path), 'not found')
            continue
        }

        $out = Join-Path $scratch ((New-Guid).ToString('N') + [System.IO.Path]::GetExtension($path))
        $err = Join-Path $scratch ((New-Guid).ToString('N') + '.err')

        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        & $Exe sync $Video $path -o $out --json 2>$err | Out-Null
        $clock.Stop()

        $text = if (Test-Path -LiteralPath $err) { Get-Content -LiteralPath $err -Raw } else { '' }

        $score = [regex]::Match($text, 'score:\s*(-?[\d.]+)')
        $offset = [regex]::Match($text, 'offset seconds:\s*(-?[\d.]+)')
        $rate = [regex]::Match($text, 'framerate scale factor:\s*(-?[\d.]+)')

        $shown = Get-DisplayedSeconds $path
        $raw = if ($score.Success) { [double]$score.Groups[1].Value } else { [double]::NaN }
        $per = if ($shown -gt 0 -and -not [double]::IsNaN($raw)) { $raw / $shown } else { [double]::NaN }

        Write-Host ("{0,-46} {1,10:N0} {2,9:N0} {3,10:N1} {4,8} {5,7}" -f `
            (Split-Path -Leaf $path).Substring(0, [Math]::Min(46, (Split-Path -Leaf $path).Length)), `
            $raw, $shown, $per, `
            $(if ($offset.Success) { $offset.Groups[1].Value } else { '—' }), `
            $(if ($rate.Success) { $rate.Groups[1].Value } else { '—' }))
    }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($Label) { Write-Host "`n$Label" }
