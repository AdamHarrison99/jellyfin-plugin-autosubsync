# Is a subtitle already aligned to its video? Re-runs the engine and reports what it still wants
# to move. A subtitle the plugin synced correctly should come back at roughly zero.
#
#   .\agentic\tools\check-in-sync.ps1 -Video <path> -Subtitle <path>
#
# A large answer here means the shipped result is wrong, not that the engine disagrees with itself:
# the plugin runs this same engine against this same audio.

param(
    [Parameter(Mandatory = $true)][string]$Video,
    [Parameter(Mandatory = $true)][string]$Subtitle,
    [string]$Exe,
    [string]$OutPath
)

if (-not $Exe) { $Exe = Join-Path $PSScriptRoot '..\payload\assy-cli\win-x64\assy-cli.exe' }
$ffprobe = Join-Path $PSScriptRoot 'ffmpeg\ffprobe.exe'

foreach ($path in @($Exe, $Video, $Subtitle)) {
    if (-not (Test-Path -LiteralPath $path)) { Write-Error "not found: $path"; exit 2 }
}

function Get-Cues([string]$path) {
    $stamps = Select-String -LiteralPath $path -Pattern '(\d{2}):(\d{2}):(\d{2})[,.](\d{3}) *-->'
    if (-not $stamps) { return $null }
    $toMs = {
        param($m)
        ((([int]$m.Groups[1].Value * 60) + [int]$m.Groups[2].Value) * 60 + [int]$m.Groups[3].Value) * 1000 +
        [int]$m.Groups[4].Value
    }
    [pscustomobject]@{
        Count = $stamps.Count
        First = & $toMs $stamps[0].Matches[0]
        Last  = & $toMs $stamps[-1].Matches[0]
    }
}

function Show-Clock([long]$ms) { '{0}m{1:00}s' -f [math]::Floor($ms / 60000), [math]::Floor(($ms % 60000) / 1000) }

$out = if ($OutPath) { $OutPath } else { Join-Path ([IO.Path]::GetTempPath()) ("insync-" + [Guid]::NewGuid().ToString('N') + '.srt') }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = (Resolve-Path -LiteralPath $Exe).Path
$psi.Arguments = @('--no-color', 'sync', """$Video""", """$Subtitle""", '-o', """$out""",
                   '-t', 'ffsubsync', '--json', '--encoding', 'same_as_input', '--no-prefix') -join ' '
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardErrorEncoding = [System.Text.Encoding]::UTF8

$process = [Diagnostics.Process]::Start($psi)
$null = $process.StandardOutput.ReadToEndAsync()
$err = $process.StandardError.ReadToEndAsync()
$process.WaitForExit()

$stderr = $err.Result
$reported = ($stderr -split "`n" | Select-String 'offset seconds|framerate scale factor').Line

$before = Get-Cues $Subtitle
$after = if (Test-Path -LiteralPath $out) { Get-Cues $out } else { $null }

"subtitle : $(Split-Path $Subtitle -Leaf)"
if (Test-Path -LiteralPath $ffprobe) {
    $seconds = & $ffprobe -v error -show_entries format=duration -of csv=p=0 $Video
    if ($seconds) { "runtime  : $(Show-Clock ([long]([double]$seconds * 1000)))" }
}
if ($before) { "cues     : $($before.Count), first $(Show-Clock $before.First), last $(Show-Clock $before.Last)" }
foreach ($line in $reported) { "engine   : $($line.Trim())" }

if ($after) {
    $startShift = $after.First - $before.First
    $endShift = $after.Last - $before.Last
    "still wants: first cue {0:+#;-#;0}ms, last cue {1:+#;-#;0}ms" -f $startShift, $endShift
    if ([math]::Abs($startShift) -le 150 -and [math]::Abs($endShift) -le 150) {
        'VERDICT  : in sync'
    }
    elseif ([math]::Abs($endShift - $startShift) -gt 1000) {
        'VERDICT  : DRIFTS - the two ends disagree, so this needs a rate change, not a shift'
    }
    else {
        'VERDICT  : OUT BY A CONSTANT SHIFT'
    }
    if ($OutPath) { "output   : $out" } else { Remove-Item -LiteralPath $out -Force }
}
else {
    'VERDICT  : engine produced no output'
}
