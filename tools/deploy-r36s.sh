#!/usr/bin/env bash
# Build an R36S-ready Godot/.NET export. No device, SSH config, or private Godot fork required.
# Usage: GODOT_BIN=/path/to/Godot_v4.5.1-stable_mono_linux.x86_64 tools/deploy-r36s.sh
# Then: tools/install-port.sh [ark@device-ip]  (creates an offline ZIP; optionally installs it)
set -euo pipefail
cd "$(dirname "$0")/.."

BIN="${GODOT_BIN:-godot}"
OUT=build451
PLAYER="$OUT/verdant-crown.arm64"
PAYLOAD="$OUT/data_VerdantCrown_linuxbsd_arm64"
for tool in dotnet file readelf; do
  command -v "$tool" >/dev/null || { echo "Missing required tool: $tool" >&2; exit 1; }
done
command -v "$BIN" >/dev/null || { echo "Godot not found: $BIN (set GODOT_BIN to the official 4.5.1 .NET editor)" >&2; exit 1; }
version=$("$BIN" --headless --version)
[[ "$version" == 4.5.1.stable.mono.* ]] || {
  echo "Expected official Godot 4.5.1 .NET editor; got: $version" >&2; exit 1;
}
# The matching 4.5.1.stable.mono templates must be installed through the editor.
# A newer player may require glibc unavailable on ArkOS; the assertion below catches this.
echo "== build (.NET) =="
dotnet build -v q
echo "== export with $version =="
rm -rf "$OUT"
mkdir -p "$OUT"
if ! "$BIN" --headless --path . --export-release R36S "$PLAYER" > "$OUT/export.log" 2>&1; then
  echo "Export failed; see $OUT/export.log" >&2
  tail -30 "$OUT/export.log" >&2
  exit 1
fi
file "$PLAYER" | grep -q 'ARM aarch64' || { echo 'Export is not ARM aarch64' >&2; exit 1; }
test -s "$OUT/verdant-crown.pck" || { echo 'Missing game .pck' >&2; exit 1; }
test -s "$PAYLOAD/libcoreclr.so" && test -s "$PAYLOAD/VerdantCrown.runtimeconfig.json" || {
  echo 'Missing self-contained .NET payload (check the classic .sln and .NET templates)' >&2; exit 1;
}
file "$PAYLOAD/libcoreclr.so" | grep -q 'ARM aarch64' || { echo 'Bundled .NET is not ARM aarch64' >&2; exit 1; }
max_glibc=$(readelf --version-info "$PLAYER" | grep -oE 'GLIBC_[0-9]+\.[0-9]+' | sort -Vu | tail -1)
test -n "$max_glibc" || { echo 'Unable to check player glibc requirements' >&2; exit 1; }
if [[ "$(printf '%s\n' "$max_glibc" GLIBC_2.30 | sort -V | tail -1)" != GLIBC_2.30 ]]; then
  echo "Player needs $max_glibc; tested ArkOS device has GLIBC_2.30" >&2; exit 1
fi
echo "Export verified: ARM64 player, .pck, self-contained ARM64 .NET payload, max $max_glibc"
echo "Next: tools/install-port.sh (offline ZIP) or tools/install-port.sh ark@<device-ip> (ZIP + SSH install)"
