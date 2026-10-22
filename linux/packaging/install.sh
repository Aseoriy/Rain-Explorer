#!/usr/bin/env bash
# Install (or remove) Rain Explorer for the current user only. No root needed.
#
#   ./install.sh               install from a build folder (the one made by packaging/build.sh)
#   ./install.sh --uninstall   remove the app, launcher and icons (settings are kept)
#
# Installs to ~/.local/share/rain-explorer, adds ~/.local/bin/rain-explorer and a menu
# entry. It does NOT make Rain your default file manager; choose that yourself in
# System Settings (KDE: Default Applications -> File Manager) if you want it.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
APP_DIR="$DATA/rain-explorer"
BIN_DIR="$HOME/.local/bin"
DESKTOP_FILE="$DATA/applications/rain-explorer.desktop"
ICON_ROOT="$DATA/icons/hicolor"
REVEAL_SERVICE="$DATA/dbus-1/services/org.freedesktop.FileManager1.service"

refresh_caches() {
    command -v update-desktop-database >/dev/null && update-desktop-database "$DATA/applications" 2>/dev/null || true
    command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$ICON_ROOT" 2>/dev/null || true
    command -v kbuildsycoca6 >/dev/null && kbuildsycoca6 >/dev/null 2>&1 || true
}

if [[ "${1:-}" == "--uninstall" ]]; then
    pkill -x RainExplorer 2>/dev/null || true
    rm -rf "$APP_DIR"
    rm -f "$BIN_DIR/rain-explorer" "$DESKTOP_FILE"
    if [[ -f "$REVEAL_SERVICE" && ! -L "$REVEAL_SERVICE" ]] &&
        [[ "$(head -n1 "$REVEAL_SERVICE")" == '# Rain Explorer FileManager1 integration' ]]; then
        rm -f "$REVEAL_SERVICE"
    fi
    find "$ICON_ROOT" -name 'rain-explorer.png' -delete 2>/dev/null || true
    refresh_caches
    echo "Rain Explorer was removed. Your settings in ~/.config/RainExplorer were kept."
    exit 0
fi

# Accept either the build folder layout (app/ + packaging/) or the source tree.
if [[ -x "$HERE/app/RainExplorer" ]]; then
    SRC_APP="$HERE/app"; SRC_PKG="$HERE/packaging"
else
    echo "Run packaging/build.sh first, then run install.sh from the dist/rain-explorer-*/ folder." >&2
    exit 1
fi

echo "Installing Rain Explorer to $APP_DIR…"
pkill -x RainExplorer 2>/dev/null || true
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR" "$BIN_DIR" "$DATA/applications"
cp -r "$SRC_APP/." "$APP_DIR/"
ln -sf "$APP_DIR/RainExplorer" "$BIN_DIR/rain-explorer"

for icon in "$SRC_PKG"/icons/rain-explorer-*.png; do
    size="$(basename "$icon" .png)"; size="${size##*-}"
    mkdir -p "$ICON_ROOT/${size}x${size}/apps"
    cp "$icon" "$ICON_ROOT/${size}x${size}/apps/rain-explorer.png"
done

sed "s#^Exec=rain-explorer#Exec=$APP_DIR/RainExplorer#" "$SRC_PKG/rain-explorer.desktop" > "$DESKTOP_FILE"
refresh_caches

echo "Done. Launch \"Rain Explorer\" from your app menu, or run: rain-explorer"
case ":$PATH:" in *":$BIN_DIR:"*) ;; *) echo "(Add $BIN_DIR to your PATH to use the rain-explorer command.)";; esac
