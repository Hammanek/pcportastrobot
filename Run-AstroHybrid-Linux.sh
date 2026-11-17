#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RUN_DIR="$ROOT/Build/RunHybrid"
EXE_NAME="${ASTROHYBRID_EXE:-AstroBot-NonRT-SoftAssert.exe}"

if [ ! -f "$RUN_DIR/$EXE_NAME" ]; then
    EXE_NAME="AstroBot.exe"
fi

mkdir -p "$ROOT/Logs"

export APS5_GPU_ENGINE="${APS5_GPU_ENGINE:-kyty}"
export ASTROHYBRID_CACHE_TITLE_ID="${ASTROHYBRID_CACHE_TITLE_ID:-PPSA21567}"
export KYTY_GPU_HOST_MODULE="${KYTY_GPU_HOST_MODULE:-astrohybrid_host.dll}"
export KYTY_GPU_WIDTH="${KYTY_GPU_WIDTH:-1920}"
export KYTY_GPU_HEIGHT="${KYTY_GPU_HEIGHT:-1080}"
export KYTY_BUFFER_RANGE_FIXES="${KYTY_BUFFER_RANGE_FIXES:-1}"
export ASTROHYBRID_COMMIT_EPOCH_FASTPATH="${ASTROHYBRID_COMMIT_EPOCH_FASTPATH:-0}"
export APS5_SAVEDATA_TRANSFER_FALLBACK="${APS5_SAVEDATA_TRANSFER_FALLBACK:-1}"
export ASTROHYBRID_GAME_SPEED="${ASTROHYBRID_GAME_SPEED:-1}"
export WINEDLLOVERRIDES="${WINEDLLOVERRIDES:-astrohybrid_host=n,b;kyty_gpu=n,b}"

EXTRA_ARGS=()
if [ "${ASTROHYBRID_SKIP_INTRO:-0}" = "1" ]; then
    EXTRA_ARGS+=("-skipIntro")
fi

cd "$RUN_DIR"
exec "${WINE:-wine}" "$RUN_DIR/$EXE_NAME" "${EXTRA_ARGS[@]}" "$@" >"$ROOT/Logs/hybrid-run.out.log" 2>"$ROOT/Logs/hybrid-run.err.log"
