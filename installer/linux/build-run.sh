#!/usr/bin/env bash
# Build a self-extracting .run installer from a published Linux build.
#   build-run.sh <publish-dir> <version> <output.run>
# Works on any distribution (Arch, Fedora, ...) without dpkg.
set -euo pipefail

if [ $# -ne 3 ]; then
    echo "usage: $0 <publish-dir> <version> <output.run>" >&2
    exit 2
fi

PUBLISH=$(cd "$1" && pwd)
VERSION=$2
OUT=$3
HERE=$(cd "$(dirname "$0")" && pwd)
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT

mkdir -p "$STAGE/payload"
cp -a "$PUBLISH/." "$STAGE/payload/"
rm -f "$STAGE/payload"/*.pdb
cp "$HERE/cd-saveeditor.desktop" "$STAGE/payload/cd-saveeditor.desktop"
cp "$HERE/../../assets/icon-256.png" "$STAGE/payload/cd-saveeditor.png"

mkdir -p "$(dirname "$OUT")"
cat > "$OUT" <<EOF
#!/bin/sh
# CD-SaveEditor $VERSION self-extracting installer
#   sudo sh $(basename "$OUT")              install to /opt/cd-saveeditor
#   sudo sh $(basename "$OUT") --uninstall  remove it
set -e
PREFIX="\${PREFIX:-/opt/cd-saveeditor}"
BINDIR="\${BINDIR:-/usr/local/bin}"
APPDIR="\${APPDIR:-/usr/share/applications}"
ICONDIR="\${ICONDIR:-/usr/share/icons/hicolor/256x256/apps}"

if [ "\$1" = "--uninstall" ]; then
    rm -rf "\$PREFIX"
    rm -f "\$BINDIR/cd-saveeditor" "\$APPDIR/cd-saveeditor.desktop" "\$ICONDIR/cd-saveeditor.png"
    echo "CD-SaveEditor removed."
    exit 0
fi

[ "\$(id -u)" -eq 0 ] || echo "note: not running as root; installing into \$PREFIX may fail (try sudo)"
mkdir -p "\$PREFIX" "\$BINDIR" "\$APPDIR" "\$ICONDIR"

SKIP=\$(awk '/^__ARCHIVE_BELOW__/ {print NR + 1; exit 0}' "\$0")
tail -n +\$SKIP "\$0" | tar xz -C "\$PREFIX"
chmod 755 "\$PREFIX/CD-SaveEditor"
ln -sf "\$PREFIX/CD-SaveEditor" "\$BINDIR/cd-saveeditor"
install -m 755 "\$PREFIX/cd-saveeditor.png" "\$ICONDIR/cd-saveeditor.png"
install -m 644 "\$PREFIX/cd-saveeditor.desktop" "\$APPDIR/cd-saveeditor.desktop"
rm -f "\$PREFIX/cd-saveeditor.png" "\$PREFIX/cd-saveeditor.desktop"

update-desktop-database "\$APPDIR" 2>/dev/null || true
gtk-update-icon-cache -q "\$(dirname "\$(dirname "\$ICONDIR")")" 2>/dev/null || true
echo "CD-SaveEditor installed to \$PREFIX (run: cd-saveeditor)"
exit 0
__ARCHIVE_BELOW__
EOF
tar -C "$STAGE/payload" -czf - . >> "$OUT"
chmod 755 "$OUT"
echo "built $OUT"
