#requires -Version 5.1
<#
.SYNOPSIS
    Builds a matrix of test videos that differ only in audio codec or container.

.DESCRIPTION
    Every output carries the same audio content, so any difference an engine shows between them is
    caused by the encoding and not by the material. The video stream is a 64x64 black placeholder:
    no engine reads it, and keeping it tiny makes the whole matrix local and fast.

    -Source should already be trimmed to the length you want to test with, and the subtitle you
    score against has to be trimmed to match.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$FfmpegPath
)

$ErrorActionPreference = 'Stop'

function Resolve-Ffmpeg {
    param([string]$Explicit)

    if ($Explicit) { return $Explicit }

    $probes = @(
        'C:\Program Files\Jellyfin\Server\ffmpeg.exe',
        'C:\Program Files\Jellyfin\Server\ffmpeg\ffmpeg.exe'
    )
    foreach ($p in $probes) { if (Test-Path -LiteralPath $p) { return $p } }

    $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    throw 'ffmpeg not found. Pass -FfmpegPath.'
}

# Each entry becomes one file. Extra holds whatever the encoder needs beyond a bitrate.
$audioMatrix = @(
    @{ Name = 'aac-stereo';   Codec = 'aac';        Channels = 2; Extra = @('-b:a', '192k') },
    @{ Name = 'aac-mono';     Codec = 'aac';        Channels = 1; Extra = @('-b:a', '128k') },
    @{ Name = 'ac3-51';       Codec = 'ac3';        Channels = 6; Extra = @('-b:a', '448k') },
    @{ Name = 'eac3-51';      Codec = 'eac3';       Channels = 6; Extra = @('-b:a', '384k') },
    @{ Name = 'dts-51';       Codec = 'dca';        Channels = 6; Extra = @('-strict', '-2') },
    @{ Name = 'flac-stereo';  Codec = 'flac';       Channels = 2; Extra = @() },
    @{ Name = 'opus-stereo';  Codec = 'libopus';    Channels = 2; Extra = @('-b:a', '128k') },
    @{ Name = 'vorbis-stereo';Codec = 'libvorbis';  Channels = 2; Extra = @('-q:a', '5') },
    @{ Name = 'mp3-stereo';   Codec = 'libmp3lame'; Channels = 2; Extra = @('-b:a', '192k') },
    @{ Name = 'pcm-stereo';   Codec = 'pcm_s16le';  Channels = 2; Extra = @() },
    @{ Name = 'truehd-51';    Codec = 'truehd';     Channels = 6; Extra = @() }
)

# Container support is the other half: the same stereo AAC (or the codec the container mandates)
# wrapped every way Jellyfin is likely to hand the plugin.
$containerMatrix = @(
    @{ Name = 'mkv';  Ext = 'mkv';  Audio = 'aac';        Video = 'libx264' },
    @{ Name = 'mp4';  Ext = 'mp4';  Audio = 'aac';        Video = 'libx264' },
    @{ Name = 'mov';  Ext = 'mov';  Audio = 'aac';        Video = 'libx264' },
    @{ Name = 'ts';   Ext = 'ts';   Audio = 'aac';        Video = 'libx264' },
    @{ Name = 'm2ts'; Ext = 'm2ts'; Audio = 'ac3';        Video = 'libx264' },
    @{ Name = 'avi';  Ext = 'avi';  Audio = 'libmp3lame'; Video = 'mpeg4' },
    @{ Name = 'wmv';  Ext = 'wmv';  Audio = 'wmav2';      Video = 'msmpeg4v3' },
    @{ Name = 'flv';  Ext = 'flv';  Audio = 'aac';        Video = 'flv' },
    @{ Name = 'webm'; Ext = 'webm'; Audio = 'libopus';    Video = 'libvpx' },
    @{ Name = 'ogv';  Ext = 'ogv';  Audio = 'libvorbis';  Video = 'libtheora' }
)

$ffmpeg = Resolve-Ffmpeg -Explicit $FfmpegPath
if (-not (Test-Path -LiteralPath $Source)) { throw "Source not found: $Source" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host "ffmpeg : $ffmpeg"
Write-Host "source : $Source"
Write-Host "out    : $OutDir`n"

# ! Regenerated per output rather than encoded once and copied: the container matrix needs a
#   different video codec per file, so there is nothing shared to reuse.
$blackVideo = @('-f', 'lavfi', '-i', 'color=c=black:s=64x64:r=5')

$made = @()
$skipped = @()

function Invoke-Encode {
    param([string]$Label, [string[]]$Arguments, [string]$Target)

    Write-Host ("  {0,-16} " -f $Label) -NoNewline
    $output = & $ffmpeg @Arguments 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $Target)) {
        Write-Host 'unavailable' -ForegroundColor DarkYellow
        $line = ($output | Select-Object -Last 1)
        if ($line) { Write-Host "      $line" -ForegroundColor DarkGray }
        return $false
    }

    $size = (Get-Item -LiteralPath $Target).Length / 1MB
    Write-Host ("ok ({0:N1} MB)" -f $size) -ForegroundColor Green
    return $true
}

Write-Host '=== Audio codecs (all in Matroska) ==='
foreach ($a in $audioMatrix) {
    $target = Join-Path $OutDir "audio-$($a.Name).mkv"
    $args = @('-y', '-v', 'error') + $blackVideo + @(
        '-i', $Source,
        '-map', '0:v:0', '-map', '1:a:0',
        '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p',
        '-c:a', $a.Codec, '-ac', $a.Channels) + $a.Extra + @('-shortest', $target)

    if (Invoke-Encode -Label $a.Name -Arguments $args -Target $target) { $made += $target }
    else { $skipped += $a.Name }
}

Write-Host "`n=== Containers (stereo) ==="
foreach ($c in $containerMatrix) {
    $target = Join-Path $OutDir "container-$($c.Name).$($c.Ext)"
    $args = @('-y', '-v', 'error') + $blackVideo + @(
        '-i', $Source,
        '-map', '0:v:0', '-map', '1:a:0',
        '-c:v', $c.Video, '-preset', 'ultrafast', '-pix_fmt', 'yuv420p',
        '-c:a', $c.Audio, '-ac', '2', '-b:a', '160k', '-shortest', $target)

    # ! Only libx264 and libvpx take -preset; the rest reject it outright.
    if ($c.Video -notin @('libx264', 'libvpx')) {
        $args = $args | Where-Object { $_ -ne '-preset' -and $_ -ne 'ultrafast' }
    }

    if (Invoke-Encode -Label $c.Name -Arguments $args -Target $target) { $made += $target }
    else { $skipped += $c.Name }
}

Write-Host "`nBuilt $($made.Count) files in $OutDir"
if ($skipped.Count -gt 0) {
    Write-Host "This ffmpeg cannot produce: $($skipped -join ', ')" -ForegroundColor DarkYellow
}
