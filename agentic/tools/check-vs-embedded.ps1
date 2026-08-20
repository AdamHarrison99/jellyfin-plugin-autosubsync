# Is an external subtitle aligned to the video's own embedded track? Compares cue start times
# against the embedded subtitle stream, which was authored to this video and so is ground truth.
#
#   .\agentic\tools\check-vs-embedded.ps1 -Video <path> -Subtitle <path>
#
# Independent of the sync engine and of the audio check, so it can judge either one. Image-based
# embedded tracks (VobSub, PGS) work: only the timings are read, never the pictures.
#
# Reads two sampled windows rather than the whole file, so it stays cheap on a remote share.

param(
    [Parameter(Mandatory = $true)][string]$Video,
    [Parameter(Mandatory = $true)][string]$Subtitle,
    [int]$Stream = -1,
    [int]$WindowSeconds = 300,
    [string]$FFprobe
)

if (-not $FFprobe) { $FFprobe = Join-Path $PSScriptRoot 'ffmpeg\ffprobe.exe' }

foreach ($path in @($FFprobe, $Video, $Subtitle)) {
    if (-not (Test-Path -LiteralPath $path)) { Write-Error "not found: $path"; exit 2 }
}

function Get-ExternalCues([string]$path) {
    $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    $times = New-Object System.Collections.Generic.List[double]
    foreach ($m in [regex]::Matches($text, '(\d{1,2}):(\d{2}):(\d{2})[,.](\d{1,3}) *-->')) {
        $times.Add(([int]$m.Groups[1].Value * 3600) + ([int]$m.Groups[2].Value * 60) +
                   [int]$m.Groups[3].Value + ([int]$m.Groups[4].Value.PadRight(3, '0') / 1000))
    }
    foreach ($m in [regex]::Matches($text, '(?m)^Dialogue:[^,]*,(\d):(\d{2}):(\d{2})\.(\d{2}),')) {
        $times.Add(([int]$m.Groups[1].Value * 3600) + ([int]$m.Groups[2].Value * 60) +
                   [int]$m.Groups[3].Value + ([int]$m.Groups[4].Value / 100))
    }
    $times.Sort()
    return $times
}

function Get-EmbeddedCues([double]$startSeconds, [int]$index) {
    $interval = if ($startSeconds -le 0) { "%+$WindowSeconds" } else { "${startSeconds}%+$WindowSeconds" }
    $raw = & $FFprobe -v error -select_streams "s:$index" -read_intervals $interval `
        -show_entries packet=pts_time -of csv=p=0 -- $Video
    $times = New-Object System.Collections.Generic.List[double]
    foreach ($line in $raw) {
        $value = 0.0
        if ([double]::TryParse(($line -replace ',', ''), [ref] $value)) { $times.Add($value) }
    }
    $times.Sort()
    return $times
}

# The signed gap from each embedded cue to the nearest external cue. The median rejects the cues
# one track has and the other does not.
function Get-Offset($embedded, $external) {
    $gaps = New-Object System.Collections.Generic.List[double]
    foreach ($cue in $embedded) {
        $best = $null
        foreach ($other in $external) {
            $gap = $other - $cue
            if ($null -eq $best -or [math]::Abs($gap) -lt [math]::Abs($best)) { $best = $gap }
        }
        if ($null -ne $best -and [math]::Abs($best) -lt 30) { $gaps.Add($best) }
    }
    if ($gaps.Count -lt 5) { return $null }
    $sorted = $gaps | Sort-Object
    return [pscustomobject]@{
        Median  = $sorted[[int]($sorted.Count / 2)]
        Matched = $gaps.Count
        Total   = $embedded.Count
    }
}

$duration = [double](& $FFprobe -v error -show_entries format=duration -of csv=p=0 -- $Video)
$external = Get-ExternalCues $Subtitle
if ($external.Count -eq 0) { Write-Error 'no cues in the external subtitle'; exit 2 }

"subtitle : $(Split-Path $Subtitle -Leaf)"
"runtime  : {0:n1} min, {1} external cues" -f ($duration / 60), $external.Count

# ! A release can carry a forced track of two or three cues beside the full one. Sampling the
#   head of each is enough to tell them apart, and costs far less than counting the whole file.
if ($Stream -lt 0) {
    $count = (& $FFprobe -v error -select_streams s -show_entries stream=index -of csv=p=0 -- $Video).Count
    $best = 0
    $most = -1
    for ($i = 0; $i -lt $count; $i++) {
        $cues = (Get-EmbeddedCues 0.0 $i).Count
        if ($cues -gt $most) { $most = $cues; $best = $i }
    }
    $Stream = $best
    "track    : embedded s:$Stream of $count, $most cues in the first $WindowSeconds s"
}

$lateStart = [math]::Max(0, $duration - $WindowSeconds - 60)
$results = @()
foreach ($window in @(@{ Name = 'early'; Start = 0.0 }, @{ Name = 'late'; Start = $lateStart })) {
    $embedded = Get-EmbeddedCues $window.Start $Stream
    $offset = Get-Offset $embedded $external
    if ($null -eq $offset) {
        "{0,-6}   : {1} embedded cues, too few matches to measure" -f $window.Name, $embedded.Count
        continue
    }
    "{0,-6}   : embedded is off the external by {1:+#;-#;0} ms ({2}/{3} cues matched)" -f `
        $window.Name, ($offset.Median * 1000), $offset.Matched, $offset.Total
    $results += $offset.Median * 1000
}

if ($results.Count -eq 0) { 'VERDICT  : could not measure'; exit 0 }

$mean = ($results | Measure-Object -Average).Average
$spread = if ($results.Count -gt 1) { [math]::Abs($results[1] - $results[0]) } else { 0 }

# ! Spread first, and never averaged. A stretch about the midpoint leaves the two ends equal and
#   opposite, so the mean of a badly drifting subtitle reads as zero.
if ($spread -gt 500) {
    "VERDICT  : DRIFTS by {0:n0} ms across the runtime - needs a rate change, not a shift" -f $spread
}
elseif ([math]::Abs($mean) -le 250) {
    'VERDICT  : IN SYNC with the embedded track'
}
else {
    "VERDICT  : OUT by {0:+#;-#;0} ms - the external subtitle needs that shift" -f (-$mean)
}
