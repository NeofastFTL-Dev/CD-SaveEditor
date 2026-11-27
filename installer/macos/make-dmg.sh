#!/bin/bash
# Build a .app bundle and a .dmg from a published macOS build.
#   make-dmg.sh <publish-dir> <version> <output.dmg>
# Runs on macOS (needs hdiutil), which the CI macos-15 job provides.
set -euo pipefail

if [ $# -ne 3 ]; then
    echo "usage: $0 <publish-dir> <version> <output.dmg>" >&2
    exit 2
fi

PUBLISH=$(cd "$1" && pwd)
VERSION=$2
OUT=$3
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(pwd)
APP="$ROOT/build-macos/CD-SaveEditor.app"
DMGROOT="$ROOT/build-macos/dmg"

rm -rf "$ROOT/build-macos"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$DMGROOT"

# Move rather than copy: the runner disk is tight and publish/ is not needed again.
mv "$PUBLISH"/. "$APP/Contents/MacOS/"
rm -f "$APP/Contents/MacOS"/*.pdb
chmod +x "$APP/Contents/MacOS/CD-SaveEditor"
cp "$HERE/../../assets/icon.icns" "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>CD-SaveEditor</string>
    <key>CFBundleDisplayName</key><string>CD-SaveEditor</string>
    <key>CFBundleIdentifier</key><string>io.github.neofastftl.cd-saveeditor</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleExecutable</key><string>CD-SaveEditor</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
</dict>
</plist>
EOF

# Ad-hoc signature: the build is not notarised, but this keeps Gatekeeper's
# "app is damaged" message away for apps started from the dmg.
codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || echo "codesign unavailable, continuing unsigned"

cp -R "$APP" "$DMGROOT/"
ln -s /Applications "$DMGROOT/Applications"

mkdir -p "$(dirname "$OUT")" "$ROOT/build-macos/tmp"
rm -f "$OUT"
# hdiutil builds its scratch image in TMPDIR; point that at the workspace volume.
export TMPDIR="$ROOT/build-macos/tmp"
df -h "$TMPDIR" || true
hdiutil create -volname "CD-SaveEditor" -srcfolder "$DMGROOT" -ov -format UDZO "$OUT" >/dev/null
echo "built $OUT"
rm -rf "$DMGROOT"

# The same bundle is what the portable zip ships.
ditto -c -k --sequesterRsrc --keepParent "$APP" "${OUT%.dmg}.app.zip"
echo "built ${OUT%.dmg}.app.zip"
