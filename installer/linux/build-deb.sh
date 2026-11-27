#!/usr/bin/env bash
# Build a Debian package from a published Linux build.
#   build-deb.sh <publish-dir> <version> <arch> <output.deb>
# Only needs ar and tar, so it also runs on machines without dpkg.
set -euo pipefail

if [ $# -ne 4 ]; then
    echo "usage: $0 <publish-dir> <version> <arch> <output.deb>" >&2
    exit 2
fi

PUBLISH=$(cd "$1" && pwd)
VERSION=$2
ARCH=$3
# Resolve against the caller's directory even when dist/ does not exist yet
# (readlink -f needs the parent to exist, and we cd elsewhere to build the archive).
case "$4" in
    /*) OUT=$4 ;;
    *) OUT="$PWD/$4" ;;
esac
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(mktemp -d)
trap 'rm -rf "$ROOT"' EXIT

PKG="$ROOT/pkg"
install -d "$PKG/DEBIAN" \
    "$PKG/opt/cd-saveeditor" \
    "$PKG/usr/bin" \
    "$PKG/usr/share/applications" \
    "$PKG/usr/share/icons/hicolor/256x256/apps" \
    "$PKG/usr/share/doc/cd-saveeditor"

cp -a "$PUBLISH/." "$PKG/opt/cd-saveeditor/"
rm -f "$PKG/opt/cd-saveeditor"/*.pdb
chmod 755 "$PKG/opt/cd-saveeditor/CD-SaveEditor"
ln -s /opt/cd-saveeditor/CD-SaveEditor "$PKG/usr/bin/cd-saveeditor"

cp "$HERE/cd-saveeditor.desktop" "$PKG/usr/share/applications/cd-saveeditor.desktop"
cp "$HERE/../../assets/icon-256.png" "$PKG/usr/share/icons/hicolor/256x256/apps/cd-saveeditor.png"
cp "$HERE/../../README.md" "$PKG/usr/share/doc/cd-saveeditor/README.md"
gzip -9n "$PKG/usr/share/doc/cd-saveeditor/README.md"

cat > "$PKG/DEBIAN/control" <<EOF
Package: cd-saveeditor
Version: $VERSION
Section: games
Priority: optional
Architecture: $ARCH
Maintainer: NeofastFTL-Dev <NeofastFTL-Dev@users.noreply.github.com>
Installed-Size: $(du -sk "$PKG/opt" | cut -f1)
Depends: libx11-6, libfontconfig1, libglib2.0-0
Recommends: libice6, libsm6, libgl1
Homepage: https://github.com/NeofastFTL-Dev/CD-SaveEditor
Description: Cel Damage save editor and LiveSplit autosplitter for xemu
 Reads and writes the Cel Damage profile inside xemu's Xbox HDD image,
 unlocks the game's content, imports and exports profiles, and drives a
 LiveSplit timer from the game's own progress.
EOF

cat > "$PKG/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
update-desktop-database /usr/share/applications 2>/dev/null || true
gtk-update-icon-cache -q /usr/share/icons/hicolor 2>/dev/null || true
exit 0
EOF

cat > "$PKG/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
rm -f /usr/bin/cd-saveeditor
exit 0
EOF
chmod 755 "$PKG/DEBIAN/postinst" "$PKG/DEBIAN/prerm"

printf '2.0\n' > "$ROOT/debian-binary"
tar -C "$PKG/DEBIAN" --owner=0 --group=0 --numeric-owner -czf "$ROOT/control.tar.gz" .
tar -C "$PKG" --owner=0 --group=0 --numeric-owner --exclude=./DEBIAN \
    -czf "$ROOT/data.tar.gz" ./opt ./usr

mkdir -p "$(dirname "$OUT")"
rm -f "$OUT"
(cd "$ROOT" && ar rc "$OUT" debian-binary control.tar.gz data.tar.gz)
echo "built $OUT"
