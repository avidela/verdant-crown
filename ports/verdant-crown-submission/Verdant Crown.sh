#!/bin/bash
#
# Verdant Crown — PortMaster launcher for ArkOS on the R36S.
# Godot 4 needs PortMaster's Weston wrapper + X11/EGL shim on this device (no desktop X11).
# The headless pre-flight distinguishes player/.NET failures from display-wrapper failures.
#
XDG_DATA_HOME=${XDG_DATA_HOME:-$HOME/.local/share}
if [ -d "/opt/system/Tools/PortMaster/" ]; then
  controlfolder="/opt/system/Tools/PortMaster"
elif [ -d "/opt/tools/PortMaster/" ]; then
  controlfolder="/opt/tools/PortMaster"
elif [ -d "$XDG_DATA_HOME/PortMaster/" ]; then
  controlfolder="$XDG_DATA_HOME/PortMaster"
else
  controlfolder="/roms/ports/PortMaster"
fi

source "$controlfolder/control.txt"
[ -f "${controlfolder}/mod_${CFW_NAME}.txt" ] && source "${controlfolder}/mod_${CFW_NAME}.txt"
get_controls

GAMEDIR=/$directory/ports/verdant-crown
CONFDIR="$GAMEDIR/conf/"
mkdir -p "$CONFDIR"
cd "$GAMEDIR"

# Keep the previous run so a new launch cannot silently destroy its diagnostic log.
if [ -s "$GAMEDIR/log.txt" ]; then
  mv "$GAMEDIR/log.txt" "$GAMEDIR/log-$(date +%Y%m%d-%H%M%S).txt"
fi
exec > >(tee "$GAMEDIR/log.txt") 2>&1

$ESUDO chmod +x "$GAMEDIR/verdant-crown.arm64"

echo "=== environment"
echo "arch=$DEVICE_ARCH cfw=$CFW_NAME ram=${DEVICE_RAM}GB display=${DISPLAY_WIDTH}x${DISPLAY_HEIGHT}"
uname -a
ls -l "$GAMEDIR/verdant-crown.arm64"
ls -d "$GAMEDIR"/data_* 2>/dev/null
free -m 2>/dev/null | head -2

# Stage 1 — the player with NO display at all (no Weston, no GL): proves the arm64 ELF, the
# bundled .NET runtime and the game's own boot code before the display shim is involved.
echo "=== pre-flight: headless boot (no display)"
"$GAMEDIR/verdant-crown.arm64" --headless --quit --main-pack "$GAMEDIR/verdant-crown.pck" > "/tmp/verdant-crown-preflight.log" 2>&1
echo "pre-flight exit code: $?"
cat "/tmp/verdant-crown-preflight.log" 2>/dev/null | head -30
echo "=== end pre-flight - if 'Verdant Crown started' appears above, player + .NET are fine"

# Godot writes saves under XDG_DATA_HOME; keep them inside the port.
export XDG_DATA_HOME="$CONFDIR"
export LD_LIBRARY_PATH="$GAMEDIR/libs.${DEVICE_ARCH}:$LD_LIBRARY_PATH"
export WRAPPED_LIBRARY_PATH="$GAMEDIR/libs.${DEVICE_ARCH}"
export SDL_GAMECONTROLLERCONFIG="$sdl_controllerconfig"

weston_dir=/tmp/weston
weston_runtime="weston_pkg_0.2"
if [ ! -f "$controlfolder/libs/${weston_runtime}.squashfs" ]; then
  if [ ! -f "$controlfolder/harbourmaster" ]; then
    pm_message "This port requires the latest PortMaster, please update it from portmaster.games"
    sleep 5
    exit 1
  fi
  $ESUDO $controlfolder/harbourmaster --quiet --no-check runtime_check "${weston_runtime}.squashfs"
fi
$ESUDO mkdir -p "$weston_dir"
if [[ "$PM_CAN_MOUNT" != "N" ]]; then
    $ESUDO umount "$weston_dir" 2>/dev/null || true
fi
$ESUDO mount "$controlfolder/libs/${weston_runtime}.squashfs" "$weston_dir"

# PortMaster sets GPTOKEYB to a command STRING (possibly sudo + flags), not a file path.
# Check its first word, execute unquoted for deliberate word splitting, and log failures.
GPTOKEYB="${GPTOKEYB:-$controlfolder/gptokeyb}"
if [ -x "${GPTOKEYB%% *}" ] || command -v "${GPTOKEYB%% *}" >/dev/null 2>&1; then
  echo "=== gptokeyb: $GPTOKEYB (controls=$GAMEDIR/controls.gptk)"
  $GPTOKEYB "verdant-crown.arm64" -c "$GAMEDIR/controls.gptk" &
else
  echo "=== gptokeyb NOT STARTED (first word '${GPTOKEYB%% *}' not executable) — pad will emit NO keys"
fi
# Not every PortMaster build defines these (PM_FUNCS_VERSION 2 does not), so guard them.
command -v pm_platform_helper >/dev/null && pm_platform_helper "$GAMEDIR/verdant-crown.arm64"

echo "=== weston stage: crusty_x11egl, opengl3_es, ${DISPLAY_WIDTH}x${DISPLAY_HEIGHT}"
$ESUDO env $weston_dir/westonwrap.sh headless noop kiosk crusty_x11egl \
  VC_AUTOSHOOT="$VC_AUTOSHOOT" VC_PILOT="$VC_PILOT" \
  XDG_DATA_HOME="$CONFDIR" \
  "$GAMEDIR/verdant-crown.arm64" \
  --resolution ${DISPLAY_WIDTH}x${DISPLAY_HEIGHT} -f \
  --rendering-driver opengl3_es --audio-driver ALSA \
  --main-pack "$GAMEDIR/verdant-crown.pck"
echo "weston stage exit code: $?"

$ESUDO $weston_dir/westonwrap.sh cleanup
if [[ "$PM_CAN_MOUNT" != "N" ]]; then
    $ESUDO umount "$weston_dir" 2>/dev/null || true
fi
command -v pm_finish >/dev/null && pm_finish
