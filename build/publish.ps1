# Publishes the version built in dist\ as a GitHub release; every VDM from 1.3.0 on offers it as an update.
#   .\build.ps1 -Publish      build, then publish
#   .\build\publish.ps1       publish what is already built (for example after a failed upload)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
$version = (Select-Xml -Path src\VDM.App\VDM.App.csproj -XPath '//Version').Node.InnerText
$setup = "dist\VDM-Setup-$version.exe"
if (-not (Test-Path $setup)) { throw "$setup is missing: run .\build.ps1 first." }
# The source is on GitHub, which attaches a source zip of the tagged commit to every release.
# ---------- publishing: a GitHub release, found by every VDM through its latest.json ----------
$repo = [regex]::Match((Get-Content src\VDM.Core\Updater.cs -Raw), 'const string Repo = "([^"]+)"').Groups[1].Value
if (-not $repo -or $repo -like 'OWNER/*') { throw 'Set Updater.Repo in src\VDM.Core\Updater.cs to the GitHub repository first.' }
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = "$env:ProgramFiles\GitHub CLI\gh.exe" }
if (-not (Test-Path $gh)) { throw 'GitHub CLI is missing: winget install GitHub.cli, then gh auth login' }

# The notes are this version's section of CHANGELOG.md, one change per line.
$section = [regex]::Match((Get-Content CHANGELOG.md -Raw -Encoding utf8), '(?ms)^## ' + [regex]::Escape($version) + '\s*$(.*?)(?=^## |\z)').Groups[1].Value
$notes = (($section -split "`r?`n") | Where-Object { $_.Trim() }) -join "`n"
if (-not $notes) { throw "CHANGELOG.md has no section for $version." }

function Get-AssetInfo($file) {
    [ordered]@{
        url    = "https://github.com/$repo/releases/download/v$version/$(Split-Path $file -Leaf)"
        size   = (Get-Item $file).Length
        sha256 = (Get-FileHash $file -Algorithm SHA256).Hash
    }
}
$latest = [ordered]@{
    version   = $version
    date      = (Get-Date -Format 'yyyy-MM-dd')
    notes     = $notes
    page      = "https://github.com/$repo/releases/tag/v$version"
    windows   = Get-AssetInfo $setup  # no "mac": a Mac builds from source, so it is only told about the page
}
[IO.File]::WriteAllText((Join-Path (Get-Location) 'dist\latest.json'), ($latest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))

$body = @"
## Có gì mới
$notes

## Cài đặt
Lần đầu: tải **VDM-Setup-$version.exe** ở mục Assets bên dưới, chạy rồi làm theo hướng dẫn trong README.
Đã có VDM 1.3.0 trở lên: không cần tải, VDM tự báo bản mới, bấm **Cập nhật ngay** là xong.

## Kiểm tra file
Bản chính thức chỉ có ở trang này. SHA-256 của VDM-Setup-$version.exe:
``$($latest.windows.sha256)``
"@
& $gh release create "v$version" $setup 'dist\latest.json' --repo $repo --title "VDM $version" --notes $body
if ($LASTEXITCODE) { throw 'Publishing the release failed.' }
"Published https://github.com/$repo/releases/tag/v$version"
