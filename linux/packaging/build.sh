#!/usr/bin/env bash
# Build a self-contained Linux release of Rain Explorer and installable packages.
#
#   packaging/build.sh            -> dist/rain-explorer-<version>-linux-x64/ and a .tar.gz next to it
#   RID=linux-arm64 packaging/build.sh   (other architectures)
# When dpkg-deb is available, also build a Debian/Ubuntu .deb.
#
# The output bundles the .NET runtime, so the target machine needs no .NET install.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${RID:-linux-x64}"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
[[ -x "$DOTNET" ]] || { echo "dotnet SDK 10 not found (set DOTNET=/path/to/dotnet)" >&2; exit 1; }

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Main/RainExplorer.csproj" | head -n1)"
NAME="rain-explorer-${VERSION}-${RID}"
OUT="$ROOT/dist/$NAME"

echo "Building Rain Explorer $VERSION for $RID…"
rm -rf "$OUT"
"$DOTNET" publish "$ROOT/Main/RainExplorer.csproj" -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false -p:DebugType=none -o "$OUT/app"

# Desktop integration files travel with the build so install.sh works from the tarball.
mkdir -p "$OUT/packaging"
cp "$ROOT/packaging/rain-explorer.desktop" "$OUT/packaging/"
cp -r "$ROOT/packaging/icons" "$OUT/packaging/"
cp "$ROOT/packaging/install.sh" "$OUT/install.sh"
chmod +x "$OUT/install.sh" "$OUT/app/RainExplorer"

tar -C "$ROOT/dist" -czf "$ROOT/dist/$NAME.tar.gz" "$NAME"
echo "Done:"
echo "  $OUT"
echo "  $ROOT/dist/$NAME.tar.gz"

PACKAGES=("$NAME.tar.gz")
if command -v dpkg-deb >/dev/null && [[ "$RID" == linux-x64 || "$RID" == linux-arm64 ]]; then
    RID="$RID" "$ROOT/packaging/build-deb.sh" "$OUT"
    DEB_VERSION="${VERSION/-linux-/~linux.}"
    case "$RID" in linux-x64) DEB_ARCH=amd64 ;; linux-arm64) DEB_ARCH=arm64 ;; esac
    PACKAGES+=("rain-explorer_${DEB_VERSION}_${DEB_ARCH}.deb")
else
    echo "Skipping .deb (requires dpkg-deb and a linux-x64 or linux-arm64 release)."
fi
(cd "$ROOT/dist" && sha256sum "${PACKAGES[@]}" > "$NAME.sha256")
echo "  $ROOT/dist/$NAME.sha256"
