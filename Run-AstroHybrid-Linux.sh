#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WINE="${WINE:-wine}"

if [ ! -f "$ROOT/Build/RunHybrid/AstroBot.exe" ]; then
    echo "Build/RunHybrid not found: run setup first" >&2
    exit 1
fi

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
