#!/usr/bin/env bash
# Turn a self-contained publish folder into a Debian/Ubuntu app package.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${RID:-linux-x64}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Main/RainExplorer.csproj" | head -n1)"
PUBLISH="${1:-$ROOT/dist/rain-explorer-${VERSION}-${RID}}"
DEB_VERSION="${VERSION/-linux-/~linux.}"

case "$RID" in
    linux-x64) ARCH=amd64 ;;
    linux-arm64) ARCH=arm64 ;;
    *) echo "Debian packaging supports linux-x64 and linux-arm64, not $RID." >&2; exit 1 ;;
esac
command -v dpkg-deb >/dev/null || { echo "dpkg-deb is required to build a .deb." >&2; exit 1; }
[[ -x "$PUBLISH/app/RainExplorer" ]] || { echo "Build the self-contained release first." >&2; exit 1; }

mkdir -p "$ROOT/dist"
STAGE="$(mktemp -d "$ROOT/dist/.rain-deb-XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
OUTPUT="$ROOT/dist/rain-explorer_${DEB_VERSION}_${ARCH}.deb"
mkdir -p "$STAGE/DEBIAN" "$STAGE/opt/rain-explorer" "$STAGE/usr/bin" \
    "$STAGE/usr/share/applications" "$STAGE/usr/share/doc/rain-explorer"
cp -a "$PUBLISH/app/." "$STAGE/opt/rain-explorer/"
ln -s /opt/rain-explorer/RainExplorer "$STAGE/usr/bin/rain-explorer"
cp "$ROOT/packaging/rain-explorer.desktop" "$STAGE/usr/share/applications/"
if [[ -f "$ROOT/README.md" ]]; then
    cp "$ROOT/README.md" "$STAGE/usr/share/doc/rain-explorer/README.md"
fi

for icon in "$ROOT"/packaging/icons/rain-explorer-*.png; do
    size="$(basename "$icon" .png)"; size="${size##*-}"
    icon_dir="$STAGE/usr/share/icons/hicolor/${size}x${size}/apps"
    mkdir -p "$icon_dir"
    cp "$icon" "$icon_dir/rain-explorer.png"
done

cat > "$STAGE/DEBIAN/control" <<EOF
Package: rain-explorer
Version: $DEB_VERSION
Architecture: $ARCH
Maintainer: Aseoriy
Section: utils
Priority: optional
Installed-Size: $(du -sk "$STAGE/opt" "$STAGE/usr" | awk '{total += $1} END {print total}')
Depends: libc6 (>= 2.31), libgcc-s1, libstdc++6, libfontconfig1, libfreetype6, libx11-6, libxcb1, libice6, libsm6, libxi6, libxrandr2, libxcursor1, zlib1g, libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu72 | libicu70, libssl3t64 | libssl3, xdg-utils, shared-mime-info
Recommends: libglib2.0-bin, fonts-dejavu-core, libgl1, libegl1
Homepage: https://rain-explorer.sailhub.fyi/
Description: Rain Explorer file manager for Linux
 Browse folders with tabs, split panes, Quick Access, previews and themes.
 Includes the .NET runtime. Adds an app-menu entry and a rain-explorer command.
 File manager defaults are chosen separately in your desktop settings.
EOF

for hook in postinst postrm; do
    cat > "$STAGE/DEBIAN/$hook" <<'EOF'
#!/bin/sh
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database /usr/share/applications >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -q -t /usr/share/icons/hicolor >/dev/null 2>&1 || true
fi
exit 0
EOF
    chmod 755 "$STAGE/DEBIAN/$hook"
done

# Package managers own the app files; all user data stays in the existing XDG folders.
chmod -R go-w "$STAGE"
chmod 755 "$STAGE" "$STAGE/DEBIAN" "$STAGE/opt/rain-explorer/RainExplorer"
dpkg-deb --root-owner-group -Zxz --build "$STAGE" "$OUTPUT"
echo "  $OUTPUT"
