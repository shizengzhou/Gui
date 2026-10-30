#!/usr/bin/env bash
# Builds libghostty-vt for Windows and installs it into the app project.
#
# Requires Zig: use the version pinned by the libghostty-vt source
# ("minimum_zig_version" in build.zig.zon; 1.4.1 needs 0.17.0).
#
#   ZIG=/c/tools/zig/zig.exe ./native/build-ghostty-vt.sh
#
# The result is native/<platform>/ghostty-vt.dll, which Gui.csproj copies to the
# output directory next to Gui.exe.
set -euo pipefail

ZIG="${ZIG:-zig}"
PLATFORM="${PLATFORM:-x64}"
VERSION="${GHOSTTY_VT_VERSION:-tip}"
WORK="${WORK:-$(mktemp -d)}"
HERE="$(cd "$(dirname "$0")" && pwd)"

case "$PLATFORM" in
  x64)   TARGET=x86_64-windows ;;
  x86)   TARGET=x86-windows ;;
  ARM64) TARGET=aarch64-windows ;;
  *) echo "unsupported platform: $PLATFORM" >&2; exit 1 ;;
esac

echo ">> fetching libghostty-vt source ($VERSION)"
curl -fL -o "$WORK/libvt.tar.gz" \
  "https://github.com/ghostty-org/ghostty/releases/download/$VERSION/libghostty-vt-source.tar.gz"
tar xzf "$WORK/libvt.tar.gz" -C "$WORK"
SRC="$(find "$WORK" -maxdepth 1 -type d -name 'libghostty-vt-*' | head -1)"

echo ">> building ghostty-vt shared library for $TARGET"
cd "$SRC"
"$ZIG" build -Demit-lib-vt -Doptimize=ReleaseFast -Dtarget="$TARGET" --prefix "$WORK/out"

DLL="$WORK/out/bin/ghostty-vt.dll"
if [ ! -f "$DLL" ]; then
  echo "build finished but $DLL is missing" >&2
  find "$WORK/out" -name "*.dll" >&2
  exit 1
fi

mkdir -p "$HERE/$PLATFORM"
cp "$DLL" "$HERE/$PLATFORM/ghostty-vt.dll"
echo ">> installed $HERE/$PLATFORM/ghostty-vt.dll"
