#!/bin/bash
# Builds "Disk Space Analyzer.app" (universal: Apple Silicon + Intel) and packs it into DiskSpaceAnalyzer-macOS.dmg
# in the repository root. Needs Xcode (or the Command Line Tools with Swift 5.7+) and python3.
#   - the version comes from src/Program.cs (AssemblyVersion), so both platforms always share one version
#   - the colour tokens, including the logo colours, are generated from src/Theme.cs (macos/gen_tokens.py)
#   - the icon is made from assets/app-1024.png (written by tools/make_icon.ps1)
#   - the dmg layout (background with an arrow to Applications) comes from dmg_art.swift and dmg_settings.py; dmgbuild
#     is installed into build/venv (needs internet access the first time)
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

# Disk image: the app on the left, a link to Applications on the right, and a background with an arrow from the app to
# Applications ("Drag to Applications"). dmgbuild (Python) writes the Finder layout (.DS_Store) itself, so no Finder
# automation is needed (AppleScript fails or times out on CI runners). The background has a 1x and a 2x version.
xcrun swiftc -O -o build/dmg_art dmg_art.swift
build/dmg_art background build/dmg-background.png 1
build/dmg_art background build/dmg-background@2x.png 2
tiffutil -cathidpicheck build/dmg-background.png build/dmg-background@2x.png -out build/background.tiff
python3 -m venv build/venv
build/venv/bin/pip install -q --disable-pip-version-check dmgbuild
rm -f ../DiskSpaceAnalyzer-macOS.dmg
build/venv/bin/dmgbuild -s dmg_settings.py -D app="$APP" -D background=build/background.tiff \
    -D icon="$APP/Contents/Resources/AppIcon.icns" "Disk Space Analyzer" ../DiskSpaceAnalyzer-macOS.dmg

echo "Built $APP ($(lipo -archs "$APP/Contents/MacOS/DiskSpaceAnalyzer")) and DiskSpaceAnalyzer-macOS.dmg ($(du -h ../DiskSpaceAnalyzer-macOS.dmg | cut -f1))"
