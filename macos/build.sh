#!/bin/bash
# Builds "Disk Space Analyzer.app" (universal: Apple Silicon + Intel) and packs it into DiskSpaceAnalyzer-macOS.dmg
# in the repository root. Needs Xcode (or the Command Line Tools with Swift 5.7+) and python3.
#   - the version comes from src/Program.cs (AssemblyVersion), so both platforms always share one version
#   - the colour tokens, including the logo colours, are generated from src/Theme.cs (macos/gen_tokens.py)
#   - the icon is made from assets/app-1024.png (written by tools/make_icon.ps1)
set -euo pipefail
cd "$(dirname "$0")"

VERSION=$(grep -oE 'AssemblyVersion\("[0-9]+\.[0-9]+\.[0-9]+' ../src/Program.cs | grep -oE '[0-9]+\.[0-9]+\.[0-9]+')
echo "Version $VERSION"

python3 gen_tokens.py

swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/DiskSpaceAnalyzer"

APP="build/Disk Space Analyzer.app"
rm -rf build
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/DiskSpaceAnalyzer"
sed "s/__VERSION__/$VERSION/g" Info.plist > "$APP/Contents/Info.plist"

ICONSET=build/AppIcon.iconset
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
    sips -z $s $s ../assets/app-1024.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
    d=$((s * 2))
    sips -z $d $d ../assets/app-1024.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# Ad-hoc signature (no Apple Developer ID): required for Apple Silicon; first launch needs right-click > Open.
codesign --force --deep --sign - "$APP"

STAGE=build/dmg
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
rm -f ../DiskSpaceAnalyzer-macOS.dmg
hdiutil create -volname "Disk Space Analyzer" -srcfolder "$STAGE" -ov -format UDZO ../DiskSpaceAnalyzer-macOS.dmg >/dev/null

echo "Built $APP ($(lipo -archs "$APP/Contents/MacOS/DiskSpaceAnalyzer")) and DiskSpaceAnalyzer-macOS.dmg ($(du -h ../DiskSpaceAnalyzer-macOS.dmg | cut -f1))"
