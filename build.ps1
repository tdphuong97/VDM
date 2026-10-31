# Builds everything: tools, browser extension, tests, the app, and the installer.
#   .\build.ps1                 full build, installer in dist\
#   .\build.ps1 -SkipTests      skip the unit tests
#   .\build.ps1 -SkipInstaller  stop after dist\app
#   .\build.ps1 -Publish        also publish a GitHub release; every VDM from 1.3.0 on offers it as an update
param([switch]$SkipTests, [switch]$SkipInstaller, [switch]$Publish)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

& .\build\fetch-tools.ps1
& .\build\build-extension.ps1

if (-not $SkipTests) {
    dotnet test tests\VDM.Core.Tests -c Release
    if ($LASTEXITCODE) { throw 'Tests failed.' }
    if (Get-Command node -ErrorAction SilentlyContinue) {
        node --test --test-reporter=spec "extension/test/*.test.mjs"
        if ($LASTEXITCODE) { throw 'Extension tests failed.' }
    }
}

$app = Join-Path $PSScriptRoot 'dist\app'
Remove-Item -Recurse -Force $app -ErrorAction SilentlyContinue
dotnet publish src\VDM.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $app
if ($LASTEXITCODE) { throw 'Publish failed.' }
Get-ChildItem $app -Recurse -Include *.pdb, *.xml | Remove-Item
"App: {0:N0} MB in {1}" -f ((Get-ChildItem $app -Recurse | Measure-Object Length -Sum).Sum / 1MB), $app

if ($SkipInstaller) { return }
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is missing: winget install JRSoftware.InnoSetup' }
$env:VDM_VERSION = (Select-Xml -Path src\VDM.App\VDM.App.csproj -XPath '//Version').Node.InnerText
& $iscc /Q build\VDM.iss
if ($LASTEXITCODE) { throw 'Installer build failed.' }
$version = $env:VDM_VERSION
$setup = "dist\VDM-Setup-$version.exe"

# ---------- the source, for building on a Mac (build/build-mac.sh) ----------
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$macZip = "dist\VDM-$version-mac-source.zip"
if (Test-Path $macZip) { [IO.File]::Delete((Join-Path $PSScriptRoot $macZip)) }
$sources = @('.gitattributes', '.gitignore', 'build.ps1', 'CHANGELOG.md', 'README.md', 'VDM.sln') +
    (Get-ChildItem build, extension\src, extension\test, src\VDM.App, src\VDM.Core, src\VDM.Mac, tests\VDM.Core.Tests -Recurse -File |
        Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules)\\' } |
        ForEach-Object { $_.FullName.Substring($PSScriptRoot.Length + 1) })
$zip = [IO.Compression.ZipFile]::Open((Join-Path $PSScriptRoot $macZip), 'Create')
try {
    foreach ($f in $sources) { [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $PSScriptRoot $f), 'VDM/' + ($f -replace '\\', '/'), 'Optimal') }
} finally { $zip.Dispose() }
Get-ChildItem $setup, $macZip | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize

if ($Publish) { & .\build\publish.ps1 }
