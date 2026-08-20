# Runs one VobSub stream through the real stager and the pinned seconv, then scores the text
# against a reference sidecar. Exists because the OCR step was the one link in the Z4 pipeline
# that no harness executed: everything up to the seconv call was verified, the call itself was not.
#
#   .\agentic\tools\check-ocr.ps1 -Sub "<library>\film.sub" -Stream 0
#   .\agentic\tools\check-ocr.ps1 -Sub "<library>\film.sub" -Stream 0 -Truth "<library>\film.eng.srt"
#   .\agentic\tools\check-ocr.ps1 -Sub "<library>\film.sub" -Stream 0 -Raw
#
# ! -Raw drops --fix-common-errors. Score raw output when asking whether OCR worked; that pass
#   rewrites unreadable glyphs into plausible characters.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sub,
    [Parameter(Mandatory)][int]$Stream,
    [string]$Language = 'eng',
    [string]$Truth,
    [switch]$Raw,
    [switch]$KeepWork,
    [string[]]$Extra = @()
)

# ! PS 5.1 wraps a native exe's stderr in ErrorRecords and seconv logs its whole run there.
$ErrorActionPreference = 'Continue'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$seconv = Join-Path $root 'agentic\payload\seconv\win-x64\seconv.exe'
$checker = Join-Path $root 'agentic\tools\vobsubcheck\bin\Release\net9.0\vobsubcheck.exe'

if (-not (Test-Path -LiteralPath $seconv)) {
    throw "No local seconv. Run: .\agentic\tools\fetch-seconv.ps1"
}

if (-not (Test-Path -LiteralPath $checker)) {
    throw "Build vobsubcheck first: dotnet build .\agentic\tools\vobsubcheck -c Release"
}

# The plugin resolves Tesseract itself; this only has to match its first probe path.
$tesseract = 'C:\Program Files\Tesseract-OCR'
if (Test-Path -LiteralPath $tesseract) {
    $env:PATH = "$tesseract;$env:PATH"
}

if (-not (Get-Command tesseract -ErrorAction SilentlyContinue)) {
    throw 'Tesseract is not on PATH and is not at its first probe path.'
}

function Get-TextStats([string]$Path) {
    $text = Get-Content -LiteralPath $Path -Raw
    $body = $text -split "`n" |
        Where-Object { $_ -notmatch '-->' -and $_.Trim() -ne '' -and $_ -notmatch '^\d+\s*$' }
    $words = ($body -join ' ') -split '\s+' | Where-Object { $_ -match '[A-Za-z]' }
    $count = [math]::Max($words.Count, 1)

    [pscustomobject]@{
        Cues          = ([regex]::Matches($text, '-->')).Count
        Words         = $words.Count
        MeanWordLen   = [math]::Round(($words | Measure-Object -Property Length -Average).Average, 2)
        PctShortWords = [math]::Round(100 * ($words | Where-Object { $_.Length -le 2 }).Count / $count, 1)
        PctAllCaps    = [math]::Round(100 * ($words | Where-Object { $_ -cmatch '^[A-Z]{2,}$' }).Count / $count, 1)
    }
}

$work = Join-Path $env:TEMP ("check-ocr-" + [guid]::NewGuid().ToString('N'))

Write-Host "staging stream $Stream ..." -NoNewline
$watch = [Diagnostics.Stopwatch]::StartNew()
$staged = & $checker $Sub $Stream $work
$split = $staged | Where-Object { $_ -match '\.idx$' } | Select-Object -Last 1
Write-Host (' {0:n1}s' -f $watch.Elapsed.TotalSeconds)

if (-not $split -or -not (Test-Path -LiteralPath $split)) {
    Write-Host 'staging failed:'
    $staged | Select-Object -Last 5
    exit 1
}

Write-Host "  $split"

$arguments = @($split, 'subrip', '--ocr-engine:tesseract', "--ocr-language:$Language")
if (-not $Raw) { $arguments += '--fix-common-errors' }
$arguments += $Extra

$output = Join-Path $work "stream$Stream.srt"
$arguments += @('--outputfilename', $output)

Write-Host ("running seconv{0} ..." -f $(if ($Raw) { ' (raw)' } else { '' }))
$watch.Restart()
& $seconv @arguments | Out-Null
$watch.Stop()

if (-not (Test-Path -LiteralPath $output)) {
    Write-Host ('OCR produced no file after {0:n1}s' -f $watch.Elapsed.TotalSeconds)
    exit 1
}

Write-Host ('OCR finished in {0:n1}s' -f $watch.Elapsed.TotalSeconds)
Write-Host ''

$rows = @()
if ($Truth) { $rows += (Get-TextStats $Truth | Add-Member Source 'reference' -PassThru) }
$rows += (Get-TextStats $output | Add-Member Source "stream $Stream" -PassThru)
$rows | Format-Table Source, Cues, Words, MeanWordLen, PctShortWords, PctAllCaps -AutoSize

# ! The tell for unusable OCR. Real dialogue runs ~4.5 characters a word with almost no
#   all-caps tokens; noise runs under 3 with half its tokens one or two characters long.
Write-Host '--- first cues ---'
Get-Content -LiteralPath $output -TotalCount 12

if ($KeepWork) {
    Write-Host "`nwork: $work"
} else {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
