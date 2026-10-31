# Builds the browser extension for Chromium browsers (Chrome, Edge, Coc Coc) and for Firefox
# into extension\dist, from the one source folder extension\src.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'extension\src'
$dist = Join-Path $root 'extension\dist'
Remove-Item -Recurse -Force $dist -ErrorAction SilentlyContinue

foreach ($target in 'chromium', 'firefox') {
    $out = Join-Path $dist $target
    New-Item -ItemType Directory -Force $out | Out-Null
    Get-ChildItem $src | Where-Object { $_.Name -notlike 'manifest*.json' } | Copy-Item -Destination $out -Recurse
    $manifest = if ($target -eq 'firefox') { 'manifest.firefox.json' } else { 'manifest.json' }
    Copy-Item (Join-Path $src $manifest) (Join-Path $out 'manifest.json')
}

# Firefox only keeps add-ons signed by Mozilla: upload this zip to addons.mozilla.org
# (unlisted, free) and save the signed file as extension\dist\vdm-firefox.xpi.
Compress-Archive -Path (Join-Path $dist 'firefox\*') -DestinationPath (Join-Path $dist 'vdm-firefox-unsigned.zip') -Force
Write-Host "extension -> $dist"
