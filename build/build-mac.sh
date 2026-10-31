#!/bin/bash
# Builds VDM for Macs with Apple silicon (M1 to M4): dist/mac/VDM.app and dist/VDM-<version>-mac-arm64.dmg.
#
# Run on the Mac, from the project folder:
#   ./build/build-mac.sh               full build, with the core tests
#   SKIP_TESTS=1 ./build/build-mac.sh  skip the tests
#
# Needs: the .NET 10 SDK (https://dotnet.microsoft.com/download) and the Xcode command line tools
# (xcode-select --install, for codesign, iconutil and hdiutil). Everything else is downloaded.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/VDM.Mac/VDM.Mac.csproj | head -1)"
RID=osx-arm64
OUT="$ROOT/dist/mac"
APP="$OUT/VDM.app"
TOOLS="$ROOT/deps/tools-mac"
CACHE="$ROOT/deps/cache-mac"
mkdir -p "$TOOLS" "$CACHE"

get() { echo "  get $1"; curl -fsSL -o "$2" "$1"; }

echo "== helper programs"
if [ ! -x "$TOOLS/yt-dlp" ]; then
  get https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_macos "$TOOLS/yt-dlp"
  chmod +x "$TOOLS/yt-dlp"
fi
for tool in ffmpeg ffprobe; do
  if [ ! -x "$TOOLS/$tool" ]; then
    get "https://ffmpeg.martin-riedl.de/redirect/latest/macos/arm64/release/$tool.zip" "$CACHE/$tool.zip"
    unzip -o -q "$CACHE/$tool.zip" -d "$TOOLS"
    chmod +x "$TOOLS/$tool"
  fi
done
if [ ! -x "$TOOLS/deno" ]; then
  get https://github.com/denoland/deno/releases/latest/download/deno-aarch64-apple-darwin.zip "$CACHE/deno.zip"
  unzip -o -q "$CACHE/deno.zip" -d "$TOOLS"
  chmod +x "$TOOLS/deno"
fi
xattr -cr "$TOOLS" 2>/dev/null || true
"$TOOLS/yt-dlp" --version >/dev/null && echo "  yt-dlp ok"

echo "== browser extension"
EXT="$ROOT/extension/dist"
rm -rf "$EXT"
for target in chromium firefox; do
  mkdir -p "$EXT/$target"
  rsync -a --exclude 'manifest*.json' extension/src/ "$EXT/$target/"
done
cp extension/src/manifest.json "$EXT/chromium/manifest.json"
cp extension/src/manifest.firefox.json "$EXT/firefox/manifest.json"
(cd "$EXT/firefox" && zip -q -r ../vdm-firefox-unsigned.zip .)

if [ "${SKIP_TESTS:-0}" != "1" ]; then
  echo "== tests"
  dotnet test tests/VDM.Core.Tests -c Release
  if command -v node >/dev/null; then node --test "extension/test/*.test.mjs"; fi
fi

echo "== publish"
rm -rf "$OUT"
mkdir -p "$OUT"
dotnet publish src/VDM.Mac -c Release -r "$RID" --self-contained true -p:DebugType=none -o "$OUT/publish"

echo "== VDM.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
rsync -a --exclude ui "$OUT/publish/" "$APP/Contents/MacOS/"
cp -R "$OUT/publish/ui" "$APP/Contents/Resources/ui"
cp -R "$EXT" "$APP/Contents/Resources/extension"
mkdir -p "$APP/Contents/MacOS/tools"
cp "$TOOLS"/yt-dlp "$TOOLS"/ffmpeg "$TOOLS"/ffprobe "$TOOLS"/deno "$APP/Contents/MacOS/tools/"
iconutil -c icns build/mac/vdm.iconset -o "$APP/Contents/Resources/vdm.icns"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>VDM</string>
  <key>CFBundleDisplayName</key><string>VDM</string>
  <key>CFBundleIdentifier</key><string>vn.vdm.app</string>
  <key>CFBundleExecutable</key><string>VDM</string>
  <key>CFBundleIconFile</key><string>vdm</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Miễn phí, không thương mại</string>
</dict>
</plist>
PLIST

echo "== sign (ad hoc)"
# Apple silicon only runs signed code. An ad hoc signature is free; a Developer ID (99 USD a year)
# would also remove the "Apple cannot check this app" question on other people's Macs.
codesign --force --deep --sign - "$APP"
codesign --verify --deep "$APP" && echo "  signature ok"

echo "== disk image"
STAGE="$OUT/dmg"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
DMG="$ROOT/dist/VDM-$VERSION-mac-arm64.dmg"
rm -f "$DMG"
hdiutil create -volname "VDM" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"
du -h "$DMG"
echo "Done: $DMG"
