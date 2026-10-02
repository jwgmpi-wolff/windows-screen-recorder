[CmdletBinding()]
param(
    [switch]$Record,
    [int]$Seconds = 5
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'publish'
& (Join-Path $PSScriptRoot 'verify-publish.ps1') -PublishDirectory $publish
if (-not $Record) {
    Write-Host 'Application startup smoke test passed. Use -Record for a DXGI media validation.'
    exit 0
}

$ffmpeg = Join-Path $publish 'Tools\ffmpeg.exe'
$ffprobe = Join-Path $publish 'Tools\ffprobe.exe'
$outputDirectory = Join-Path $publish 'Data\SmokeTest'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$output = Join-Path $outputDirectory 'dxgi-smoke.mp4'
Remove-Item -LiteralPath $output -ErrorAction SilentlyContinue

& $ffmpeg -hide_banner -loglevel warning -y `
    -f lavfi -i 'ddagrab=output_idx=0:draw_mouse=1:framerate=30' `
    -t $Seconds -vf 'hwdownload,format=bgra,format=yuv420p' `
    -an -c:v h264_mf -b:v 4M -r 30 -pix_fmt yuv420p -movflags +faststart $output
if ($LASTEXITCODE -ne 0) { throw 'DXGI smoke recording failed.' }

$probe = & $ffprobe -v error -show_entries 'format=duration:stream=codec_name,codec_type,width,height,avg_frame_rate' -of json $output |
    Out-String | ConvertFrom-Json
$video = $probe.streams | Where-Object codec_type -eq 'video' | Select-Object -First 1
if (-not $video -or $video.codec_name -ne 'h264' -or $video.width -le 0 -or $video.height -le 0) {
    throw 'ffprobe did not find a valid H.264 video stream.'
}
if ([double]$probe.format.duration -lt ($Seconds - 0.5)) {
    throw "Recorded duration was shorter than expected: $($probe.format.duration)s."
}
Write-Host "DXGI smoke recording passed: $($video.width)x$($video.height), $($video.avg_frame_rate), $($probe.format.duration)s"
