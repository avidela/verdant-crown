#!/usr/bin/env bash
# Package a flat, offline-installable PortMaster ZIP from a verified local export.
# Usage: tools/deploy-r36s.sh && tools/install-port.sh [ark@device-ip]
# With no argument, this script requires no network/device. With a target, it also
# copies the flat layout to ArkOS over SSH and restarts EmulationStation.
set -euo pipefail
cd "$(dirname "$0")/.."
OUT=build451
SUB=ports/verdant-crown-submission
LICENSES=ports/verdant-crown-install/verdant-crown/licenses
TARGET="${1:-}"
PORTS=/roms/ports
GAME_SH='Verdant Crown.sh'
BUNDLE="$PWD/$OUT/verdant-crown.zip"
for tool in zip file readelf; do
  command -v "$tool" >/dev/null || { echo "Missing required tool: $tool" >&2; exit 1; }
done
for f in "$OUT/verdant-crown.arm64" "$OUT/verdant-crown.pck" \
         "$OUT/data_VerdantCrown_linuxbsd_arm64/libcoreclr.so" \
         "$OUT/data_VerdantCrown_linuxbsd_arm64/VerdantCrown.runtimeconfig.json" \
         "$SUB/$GAME_SH" "$SUB/port.json" "$SUB/gameinfo.xml" \
         "$SUB/controls.gptk" "$SUB/screenshot.png" "$SUB/README.md" \
         LICENSE "$LICENSES/README.txt" "$LICENSES/GODOT-LICENSE.txt" \
         "$LICENSES/GODOT-COPYRIGHT.txt" "$LICENSES/DOTNET-LICENSE.txt" \
         "$LICENSES/DOTNET-THIRD-PARTY-NOTICES.txt"; do
  test -s "$f" || { echo "Missing $f; run tools/deploy-r36s.sh first" >&2; exit 1; }
done
file "$OUT/verdant-crown.arm64" | grep -q 'ARM aarch64' || { echo 'Not an ARM64 export' >&2; exit 1; }
file "$OUT/data_VerdantCrown_linuxbsd_arm64/libcoreclr.so" | grep -q 'ARM aarch64' || { echo 'Not an ARM64 .NET runtime' >&2; exit 1; }
max_glibc=$(readelf --version-info "$OUT/verdant-crown.arm64" | grep -oE 'GLIBC_[0-9]+\.[0-9]+' | sort -Vu | tail -1)
if [[ -z "$max_glibc" || "$(printf '%s\n' "$max_glibc" GLIBC_2.30 | sort -V | tail -1)" != GLIBC_2.30 ]]; then
  echo "Player glibc requirement ${max_glibc:-unknown} exceeds tested GLIBC_2.30" >&2; exit 1
fi
stage=$(mktemp -d "$OUT/.port-stage.XXXXXX")
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/verdant-crown/licenses"
cp "$SUB/$GAME_SH" "$stage/"
cp "$SUB/port.json" "$SUB/gameinfo.xml" "$SUB/controls.gptk" "$SUB/screenshot.png" "$stage/verdant-crown/"
cp "$SUB/README.md" "$stage/verdant-crown/readme.txt"
cp "$LICENSES/"*.txt "$stage/verdant-crown/licenses/"
cp LICENSE "$stage/verdant-crown/licenses/VERDANT-CROWN-LICENSE.txt"
cp "$OUT/verdant-crown.arm64" "$OUT/verdant-crown.pck" "$stage/verdant-crown/"
cp -a "$OUT/data_VerdantCrown_linuxbsd_arm64" "$stage/verdant-crown/"
chmod +x "$stage/$GAME_SH" "$stage/verdant-crown/verdant-crown.arm64"
rm -f "$BUNDLE"
(cd "$stage" && zip -q -r -X "$BUNDLE" "$GAME_SH" verdant-crown)
echo "Offline bundle: $BUNDLE"
echo 'Contents: Verdant Crown.sh beside verdant-crown/ (extract both into EASYROMS/ports/)'
if [[ -z "$TARGET" ]]; then
  exit 0
fi
for tool in ssh scp; do
  command -v "$tool" >/dev/null || { echo "Missing required tool: $tool" >&2; exit 1; }
done
ssh -o ConnectTimeout=8 "$TARGET" "mkdir -p '$PORTS/verdant-crown'" || {
  echo "Cannot connect to $TARGET; bundle remains at $BUNDLE" >&2; exit 1;
}
scp -q "$stage/$GAME_SH" "$TARGET:$PORTS/"
scp -q -r "$stage/verdant-crown/." "$TARGET:$PORTS/verdant-crown/"
ssh "$TARGET" "chmod +x '$PORTS/$GAME_SH' '$PORTS/verdant-crown/verdant-crown.arm64' && \
  grep -Fq '<path>./Verdant Crown.sh</path>' '$PORTS/verdant-crown/gameinfo.xml' && \
  test -s '$PORTS/verdant-crown/data_VerdantCrown_linuxbsd_arm64/libcoreclr.so' && \
  sudo systemctl restart emulationstation"
echo "Installed on $TARGET. Confirm Verdant Crown appears in the Ports menu; on failure read $PORTS/verdant-crown/log.txt."
