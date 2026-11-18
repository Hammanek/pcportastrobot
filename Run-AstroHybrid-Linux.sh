#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WINE="${WINE:-wine}"

# Wine marks a Bluetooth DualSense's parent devnode only as WINEBUS\WINE_COMP_HID, so SDL (inside
# kyty_gpu.dll) treats it as USB and sends USB-format output reports: no rumble, haptics or light bar
# over Bluetooth (#59). Adding the BTHENUM compatible IDs that Windows reports makes SDL see it as
# wireless. Idempotent; only touches DualSense (054C:0CE6) and DualSense Edge (054C:0DF2) entries.
# ASTROHYBRID_DS_BT_FIX=0 skips it.
fix_dualsense_bt() {
    local base='HKLM\System\CurrentControlSet\Enum\BTHENUM'
    local uuid='{00001124-0000-1000-8000-00805f9b34fb}'
    local key pid
    WINEDEBUG=-all "$WINE" reg query "$base" /s 2>/dev/null | tr -d '\r' | awk '
        /^HKEY/ { key = $0 }
        /CompatibleIDs/ && key ~ /VID_054C&PID_(0CE6|0DF2)\\/ && $0 !~ /BTHENUM/ { print key }' |
    while read -r key; do
        pid=$(sed -nE 's/.*PID_([0-9A-F]{4}).*/\1/p' <<<"$key" | tr 'A-F' 'a-f')
        WINEDEBUG=-all "$WINE" reg add "$key" /v CompatibleIDs /t REG_MULTI_SZ \
            /d "BTHENUM\\${uuid}_VID&0002054c_PID&${pid}\\0BTHENUM\\${uuid}\\0WINEBUS\\WINE_COMP_HID" /f >/dev/null &&
            echo "DualSense Bluetooth fix applied to ${key##*\\}"
    done || true
}

if [ ! -f "$ROOT/Build/RunHybrid/AstroBot.exe" ]; then
    echo "Build/RunHybrid not found: run setup first" >&2
    exit 1
fi
[ "${ASTROHYBRID_DS_BT_FIX:-1}" = "0" ] || fix_dualsense_bt

# Real-time game speed, as LauncherService does: on when setup found the frame-rate block, with its address.
features="$ROOT/Build/RunHybrid/astrohybrid-game.json"
if [ -z "${ASTROHYBRID_GAME_SPEED:-}" ] && [ -f "$features" ]; then
    if grep -Eq '"RealTimeSpeed"[[:space:]]*:[[:space:]]*false' "$features"; then
        export ASTROHYBRID_GAME_SPEED=0
    else
        export ASTROHYBRID_GAME_SPEED=1
        step=$(sed -nE 's/.*"GameStepAddress"[[:space:]]*:[[:space:]]*([0-9]+).*/\1/p' "$features" | head -n1)
        if [ -n "$step" ] && [ "$step" != "0" ] && [ -z "${ASTROHYBRID_GAME_STEP_ADDR:-}" ]; then
            export ASTROHYBRID_GAME_STEP_ADDR="$(printf '0x%x' "$step")"
        fi
    fi
fi

# Start through Run-AstroHybrid.bat, as the launcher's Play button does, so Linux gets the same defaults
# as Windows (u59-preset.cmd engine preset, trigger state, motion, priorities, executable choice, logs).
# Variables set before running this script still take precedence.
# The DLL overrides keep Wine from replacing the shipped DLLs with builtin stubs.
export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:-astrohybrid_host=n,b;kyty_gpu=n,b}"
cd "$ROOT"
exec "$WINE" cmd /c Run-AstroHybrid.bat "$@"
