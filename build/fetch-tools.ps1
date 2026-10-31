# Downloads the helper programs VDM ships with into deps\tools:
#   yt-dlp.exe  - fetches YouTube and thousands of other video sites (updates itself later)
#   ffmpeg      - merges video and audio, converts to MP3 (yt-dlp's own FFmpeg builds, shared libs)
#   deno.exe    - the JavaScript runtime yt-dlp needs for YouTube
# Run again with -Force to replace what is there.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root 'deps\tools'
$cache = Join-Path $root 'deps\cache'
New-Item -ItemType Directory -Force $tools, $cache | Out-Null

function Get-File($url, $out) {
    Write-Host "  get $url"
    Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing -Headers @{ 'User-Agent' = 'VDM-build' }
}

if ($Force -or -not (Test-Path "$tools\yt-dlp.exe")) {
    Write-Host 'yt-dlp'
    Get-File 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' "$tools\yt-dlp.exe"
}

if ($Force -or -not (Test-Path "$tools\ffmpeg.exe")) {
    Write-Host 'ffmpeg'
    $zip = Join-Path $cache 'ffmpeg.zip'
    Get-File 'https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip' $zip
    $unz = Join-Path $cache 'ffmpeg'
    Remove-Item -Recurse -Force $unz -ErrorAction SilentlyContinue
    Expand-Archive $zip $unz
    $bin = Get-ChildItem $unz -Recurse -Directory -Filter bin | Select-Object -First 1
    Get-ChildItem $bin.FullName | Where-Object { $_.Name -ne 'ffplay.exe' } | Copy-Item -Destination $tools -Force
}

if ($Force -or -not (Test-Path "$tools\deno.exe")) {
    Write-Host 'deno'
    $zip = Join-Path $cache 'deno.zip'
    Get-File 'https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip' $zip
    Expand-Archive $zip $tools -Force
}

Write-Host ''
Get-ChildItem $tools | Sort-Object Length -Descending |
    Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
"Total: {0:N0} MB" -f ((Get-ChildItem $tools | Measure-Object Length -Sum).Sum / 1MB)
