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

# Move rather than copy (the runner disk is tight, and publish/ is not needed again).
# BSD mv rejects "dir/.", so stream the tree and drop the source.
(cd "$PUBLISH" && tar cf - .) | (cd "$APP/Contents/MacOS" && tar xf -)
rm -rf "$PUBLISH"
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
LOG="$ROOT/build-macos/hdiutil.log"
BUILT=0
for attempt in 1 2 3; do
    rm -rf "$TMPDIR"/* 2>/dev/null || true
    if hdiutil create -volname "CD-SaveEditor" -srcfolder "$DMGROOT" -ov -format UDZO "$OUT" >"$LOG" 2>&1; then
        BUILT=1
        break
    fi
    echo "hdiutil create failed (attempt $attempt):"
    tail -3 "$LOG"
    df -h "$TMPDIR" || true
    rm -f "$OUT" "$OUT".temp.dmg
    sleep 5
done
if [ "$BUILT" -ne 1 ]; then
    echo "could not create $OUT" >&2
    cat "$LOG" >&2
    exit 1
fi
echo "built $OUT"
rm -rf "$DMGROOT"

# The same bundle is what the portable zip ships.
ditto -c -k --sequesterRsrc --keepParent "$APP" "${OUT%.dmg}.app.zip"
echo "built ${OUT%.dmg}.app.zip"
