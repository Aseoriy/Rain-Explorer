#!/usr/bin/env bash
# Package an existing self-contained publish as an AppImage.
# Pinned upstream tools are verified and cached; subsequent builds work offline.
# APPIMAGETOOL and APPIMAGE_RUNTIME may point to locally supplied alternatives.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${RID:-linux-x64}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Main/RainExplorer.csproj" | head -n1)"
NAME="rain-explorer-${VERSION}-${RID}"
PUBLISH="${1:-$ROOT/dist/$NAME}"
TOOL_VERSION=1.9.1
RUNTIME_VERSION=20251108
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/RainExplorer/appimage"

case "$RID" in
    linux-x64)
        TARGET_ARCH=x86_64
        RUNTIME_SHA=2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d ;;
    linux-arm64)
        TARGET_ARCH=aarch64
        RUNTIME_SHA=00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444 ;;
    *) echo "AppImage packaging supports linux-x64 and linux-arm64, not $RID." >&2; exit 1 ;;
esac
case "$(uname -m)" in
    x86_64)
        TOOL_ARCH=x86_64
        TOOL_SHA=ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0 ;;
    aarch64|arm64)
        TOOL_ARCH=aarch64
        TOOL_SHA=f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158 ;;
    *) echo "Build AppImages on an x86_64 or aarch64 host." >&2; exit 1 ;;
esac
[[ -x "$PUBLISH/app/RainExplorer" && -f "$PUBLISH/app/RainExplorer.runtimeconfig.json" ]] || {
    echo "Build the self-contained release first (packaging/build.sh)." >&2; exit 1;
}

STAGE=""
DOWNLOAD=""
cleanup() {
    [[ -z "$DOWNLOAD" ]] || rm -f -- "$DOWNLOAD"
    [[ -z "$STAGE" ]] || rm -rf -- "$STAGE"
}
trap cleanup EXIT

fetch_verified() {
    local url="$1" expected="$2" destination="$3"
    if [[ -f "$destination" ]] && printf '%s  %s\n' "$expected" "$destination" | sha256sum --check --status; then
        return
    fi
    command -v curl >/dev/null || { echo "Install curl to download the AppImage build tools." >&2; exit 1; }
    mkdir -p "$CACHE"
    DOWNLOAD="$(mktemp "$CACHE/.download-XXXXXX")"
    echo "Downloading $url"
    curl --fail --silent --show-error --location --retry 3 --connect-timeout 15 --proto '=https' --proto-redir '=https' \
        "$url" -o "$DOWNLOAD"
    printf '%s  %s\n' "$expected" "$DOWNLOAD" | sha256sum --check --status || {
        echo "AppImage tool checksum did not match: $url" >&2; exit 1;
    }
    mv -- "$DOWNLOAD" "$destination"
    DOWNLOAD=""
}

TOOL="${APPIMAGETOOL:-$CACHE/appimagetool-${TOOL_VERSION}-${TOOL_ARCH}.AppImage}"
RUNTIME="${APPIMAGE_RUNTIME:-$CACHE/runtime-${RUNTIME_VERSION}-${TARGET_ARCH}}"
if [[ -z "${APPIMAGETOOL:-}" ]]; then
    fetch_verified "https://github.com/AppImage/appimagetool/releases/download/$TOOL_VERSION/appimagetool-$TOOL_ARCH.AppImage" \
        "$TOOL_SHA" "$TOOL"
    chmod +x "$TOOL"
fi
if [[ -z "${APPIMAGE_RUNTIME:-}" ]]; then
    fetch_verified "https://github.com/AppImage/type2-runtime/releases/download/$RUNTIME_VERSION/runtime-$TARGET_ARCH" \
        "$RUNTIME_SHA" "$RUNTIME"
fi
[[ -x "$TOOL" && -f "$RUNTIME" ]] || { echo "Check APPIMAGETOOL and APPIMAGE_RUNTIME paths." >&2; exit 1; }

mkdir -p "$ROOT/dist"
STAGE="$(mktemp -d "$ROOT/dist/.rain-appimage-XXXXXX")"
APP_DIR="$STAGE/RainExplorer.AppDir"
mkdir -p "$APP_DIR/usr/lib/rain-explorer" "$APP_DIR/usr/bin" "$APP_DIR/usr/share/applications"
cp -a "$PUBLISH/app/." "$APP_DIR/usr/lib/rain-explorer/"
cp "$ROOT/packaging/AppRun" "$APP_DIR/AppRun"
cp "$ROOT/packaging/rain-explorer.desktop" "$APP_DIR/usr/share/applications/"
ln -s usr/share/applications/rain-explorer.desktop "$APP_DIR/rain-explorer.desktop"
ln -s ../lib/rain-explorer/RainExplorer "$APP_DIR/usr/bin/rain-explorer"
for icon in "$ROOT"/packaging/icons/rain-explorer-*.png; do
    size="$(basename "$icon" .png)"; size="${size##*-}"
    directory="$APP_DIR/usr/share/icons/hicolor/${size}x${size}/apps"
    mkdir -p "$directory"
    cp "$icon" "$directory/rain-explorer.png"
done
ln -s usr/share/icons/hicolor/256x256/apps/rain-explorer.png "$APP_DIR/rain-explorer.png"
ln -s rain-explorer.png "$APP_DIR/.DirIcon"
chmod +x "$APP_DIR/AppRun" "$APP_DIR/usr/lib/rain-explorer/RainExplorer"

# Extract the build tool rather than requiring FUSE on the build machine.
APPIMAGE_EXTRACT_AND_RUN=1 ARCH="$TARGET_ARCH" VERSION="$VERSION" \
    "$TOOL" --runtime-file "$RUNTIME" --comp zstd --no-appstream "$APP_DIR" "$STAGE/$NAME.AppImage"
chmod +x "$STAGE/$NAME.AppImage"
mv -- "$STAGE/$NAME.AppImage" "$ROOT/dist/$NAME.AppImage"
echo "  $ROOT/dist/$NAME.AppImage"
