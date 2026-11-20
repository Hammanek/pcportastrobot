@echo off
rem AstroHybrid: AstroBot.exe (AnyPS5 native port) rendering with Kyty's GPU stack (kyty_gpu.dll).
rem Engine selection: this launcher uses Build\RunHybrid; Run-AstroBot.bat keeps the AnyPS5 renderer
rem in Build\Run, which is the reliable fallback. Rebuild RunHybrid with
rem   python -I Tools\hybrid\make_run_hybrid.py
setlocal
set "ROOT=%~dp0"
if not exist "%ROOT%Build\RunHybrid\AstroBot.exe" (
  rem Issue #153: a third-party launcher started the game before AstroHybrid's setup ran.
  echo The game is not set up yet: Build\RunHybrid\AstroBot.exe was not found next to this script.
  echo Open AstroHybrid.exe in this folder and run its setup ^(pick your game dump^) first, then start the game again.
  echo Developers: python -I Tools\hybrid\make_run_hybrid.py builds it from a source checkout.
  exit /b 1
)

rem TDR (Timeout Detection and Recovery) workaround for Vulkan Device Lost (Error 321)
rem Enables splitting large command buffers to keep GPU work under the TDR threshold
if not defined ASTROHYBRID_TDR_WORKAROUND set "ASTROHYBRID_TDR_WORKAROUND=1"

set APS5_GPU_ENGINE=kyty
rem Keep writable commit-epoch fastpath off (read-only clean memo fastpath on) and buffer-range fixes on so cloth, blankets, and bubbles
rem do not corrupt into stray polygons (issues #30, #35).
if not defined ASTROHYBRID_COMMIT_EPOCH_FASTPATH set "ASTROHYBRID_COMMIT_EPOCH_FASTPATH=read"
if not defined KYTY_BUFFER_RANGE_FIXES set "KYTY_BUFFER_RANGE_FIXES=1"
if not defined ASTROHYBRID_OPT_1 set "ASTROHYBRID_OPT_1=1"
if not defined ASTROHYBRID_OPT_2 set "ASTROHYBRID_OPT_2=1"
if not defined KYTY_SUBMIT_MIN_INTERVAL_US set "KYTY_SUBMIT_MIN_INTERVAL_US=500"
if not defined KYTY_GFX_SLICE_DRAWS set "KYTY_GFX_SLICE_DRAWS=512"

rem Physics sub-stepping for sub-60 FPS collision issues (#125, #164)
if not defined ASTROHYBRID_PHYSICS_SUBSTEP set "ASTROHYBRID_PHYSICS_SUBSTEP=1"

rem Hang watchdog (A/B 2026-11-07, OVERNIGHT.md): the preset's KYTY_HANG_WATCHDOG=auto turns it on for
rem RTX 50 GPUs, where it costs the command processor ~1.8 ms per frame in Sky Garden (CP 33.7 -> 31.9 ms).
rem It only records diagnostics when the game hangs. ASTROHYBRID_HANG_WATCHDOG=1 (or KYTY_HANG_WATCHDOG
rem set by you) brings it back; set it when reporting a freeze.
if not defined KYTY_HANG_WATCHDOG if not "%ASTROHYBRID_HANG_WATCHDOG%"=="1" set KYTY_HANG_WATCHDOG=0

rem Enhanced hang detection for game freeze after update (#166) and other freezes
rem ASTROHYBRID_HANG_WATCHDOG=2 enables aggressive monitoring with periodic stack captures
if not defined ASTROHYBRID_HANG_WATCHDOG_INTERVAL if not "%ASTROHYBRID_HANG_WATCHDOG%"=="0" set ASTROHYBRID_HANG_WATCHDOG_INTERVAL=5000

rem Persistent shader and pipeline cache behaviour (verified 2026-11-08; see OVERNIGHT.md): without the title ID the
rem hybrid never opened Kyty's caches and compiled shaders during play (100-117 ms hitches); with it, no
rem late compiles and no frame over 80 ms. ASTROHYBRID_SHADER_CACHE=0 turns it off.
if not defined ASTROHYBRID_CACHE_TITLE_ID if not "%ASTROHYBRID_SHADER_CACHE%"=="0" set ASTROHYBRID_CACHE_TITLE_ID=PPSA21567

rem Fast load times: async asset loading with priority queues (#140)
if not defined ASTROHYBRID_FAST_LOAD set "ASTROHYBRID_FAST_LOAD=1"

rem VRAM limit for texture streaming (#114)
if not defined ASTROHYBRID_VRAM_LIMIT_MB set "ASTROHYBRID_VRAM_LIMIT_MB=0"

rem Texture streaming quality (higher = better quality, more VRAM) for #13, #145
if not defined ASTROHYBRID_TEXTURE_STREAMING_QUALITY set "ASTROHYBRID_TEXTURE_STREAMING_QUALITY=2"

rem Shader precompilation to reduce first-playthrough stutter (#83, #92)
if not defined ASTROHYBRID_SHADER_PRECOMPILE set "ASTROHYBRID_SHADER_PRECOMPILE=1"
if not defined ASTROHYBRID_PIPELINE_CACHE_WARMUP set "ASTROHYBRID_PIPELINE_CACHE_WARMUP=1"

rem Aggressive shader cache clearing on startup for #161, #157 (stale cache issues)
if not defined ASTROHYBRID_CLEAR_SHADER_CACHE_ON_STARTUP set "ASTROHYBRID_CLEAR_SHADER_CACHE_ON_STARTUP=0"

rem Memory pressure handling for #161, #157 (high VRAM usage)
if not defined ASTROHYBRID_MEMORY_PRESSURE_HANDLING set "ASTROHYBRID_MEMORY_PRESSURE_HANDLING=1"

if exist "%ROOT%Build\RunHybrid\u59-preset.cmd" call "%ROOT%Build\RunHybrid\u59-preset.cmd"

rem Prefer the soft-assert NonRT build for the command-buffer overflow path (issue #25).
rem Callers can still override ASTROHYBRID_EXE explicitly; older runtimes fall back safely.
if not defined ASTROHYBRID_EXE if exist "%ROOT%Build\RunHybrid\AstroBot-NonRT-SoftAssert.exe" set ASTROHYBRID_EXE=AstroBot-NonRT-SoftAssert.exe
if not defined ASTROHYBRID_EXE if exist "%ROOT%Build\RunHybrid\AstroBot-NonRT.exe" set ASTROHYBRID_EXE=AstroBot-NonRT.exe
if not defined ASTROHYBRID_EXE set ASTROHYBRID_EXE=AstroBot.exe
set KYTY_GPU_HOST_MODULE=astrohybrid_host.dll

rem scePadGetTriggerEffectState answers from the game's trigger effect and the trigger travel, as a
rem DualSense does; Astro Bot ignores L2/R2 while it reads "no effect" (2026-11-07). Rumble, light bar and
rem trigger forces reach the controller through Kyty (ABI 1.4). APS5_PAD_TRIGGER_STATE=0 restores zeros.
if not defined APS5_PAD_TRIGGER_STATE set APS5_PAD_TRIGGER_STATE=1

rem Older Astro Bot builds (e.g. PPSA21564 01.007.000, issue #27) call sceSaveDataTransferringMount
rem as their initial save mount; opt into the mount3 fallback unless explicitly disabled.
if not defined APS5_SAVEDATA_TRANSFER_FALLBACK set APS5_SAVEDATA_TRANSFER_FALLBACK=1

rem Motion sensors (2026-11-12, OVERNIGHT.md): the controller's accelerometer and gyroscope reach the game
rem (shake and tilt moves); before, the game always read a controller at rest. ASTROHYBRID_PAD_MOTION=0 restores that.
if not defined ASTROHYBRID_PAD_MOTION set ASTROHYBRID_PAD_MOTION=1

rem Vibration strength of pads other than a DualSense (Xbox, Switch, generic), as percent of what the game
rem asks for: ASTROHYBRID_RUMBLE_SCALE=0..200 (default 100). ASTROHYBRID_RUMBLE_SWITCH_FLOOR=n (default 0)
rem lifts a Switch-type pad's motor to at least n of 255; the launchers pass 96 scaled by the strength, so a
rem light setting stays light on Joy-Cons and Switch Pro pads (issue #36).
rem Priority (A/B 2026-11-07, OVERNIGHT.md): the command processor at THREAD_PRIORITY_HIGHEST and the
rem process above normal keep other programs off the reserved CP core. ASTROHYBRID_PRIORITY=normal and
rem KYTY_CP_PRIORITY=1 restore the previous behaviour.
if not defined KYTY_CP_PRIORITY set KYTY_CP_PRIORITY=2

rem Additional stability options for various crash issues
rem ASTROHYBRID_SAFE_MODE=1 disables all experimental features for maximum stability
if not defined ASTROHYBRID_SAFE_MODE set "ASTROHYBRID_SAFE_MODE=0"

rem Enable debug validation layers for Vulkan (helps diagnose #154, #65, #6, #166)
if not defined ASTROHYBRID_DEBUG_LAYERS set "ASTROHYBRID_DEBUG_LAYERS=0"

rem Force single-threaded command submission for debugging command buffer issues (#25)
if not defined ASTROHYBRID_SINGLE_THREADED_SUBMIT set "ASTROHYBRID_SINGLE_THREADED_SUBMIT=0"

rem Safe mode: disable experimental features
if "%ASTROHYBRID_SAFE_MODE%"=="1" (
    set "ASTROHYBRID_TDR_WORKAROUND=0"
    set "ASTROHYBRID_FAST_LOAD=0"
    set "ASTROHYBRID_SHADER_PRECOMPILE=0"
    set "ASTROHYBRID_PIPELINE_CACHE_WARMUP=0"
    set "ASTROHYBRID_PHYSICS_SUBSTEP=0"
    set "ASTROHYBRID_OPT_1=0"
    set "ASTROHYBRID_OPT_2=0"
    set "KYTY_HANG_WATCHDOG=0"
    echo [Safe Mode] Disabled experimental features
)

rem Debug validation layers: enable Vulkan validation layers for debugging
if "%ASTROHYBRID_DEBUG_LAYERS%"=="1" (
    set "VK_INSTANCE_LAYERS=VK_LAYER_KHRONOS_validation"
    set "VK_LAYER_KHRONOS_validation_ENABLE=1"
    set "VK_LAYER_KHRONOS_validation_DEBUG_ACTION=VK_DBG_LAYER_ACTION_LOG_MSG,VK_DBG_LAYER_ACTION_BREAK"
    echo [Debug] Vulkan validation layers enabled
)

rem Single-threaded submit for debugging command buffer issues
if "%ASTROHYBRID_SINGLE_THREADED_SUBMIT%"=="1" (
    set "KYTY_SUBMIT_MIN_INTERVAL_US=0"
    set "KYTY_GFX_SLICE_DRAWS=0"
    echo [Debug] Single-threaded command submission enabled
)

rem Clear shader cache on startup for stale cache issues (#161, #157)
if "%ASTROHYBRID_CLEAR_SHADER_CACHE_ON_STARTUP%"=="1" (
    if exist "%ROOT%Build\RunHybrid\shader_cache.bin" del "%ROOT%Build\RunHybrid\shader_cache.bin"
    if exist "%ROOT%Build\RunHybrid\pipeline_cache.bin" del "%ROOT%Build\RunHybrid\pipeline_cache.bin"
    if exist "%ROOT%Build\RunHybrid\cache" rmdir /s /q "%ROOT%Build\RunHybrid\cache" 2>nul
    echo [Startup] Shader/pipeline cache cleared
)

rem Memory pressure handling for high VRAM usage (#161, #157)
if "%ASTROHYBRID_MEMORY_PRESSURE_HANDLING%"=="1" (
    rem Auto-detect GPU VRAM if not explicitly set (0 = auto)
    if "%ASTROHYBRID_VRAM_LIMIT_MB%"=="0" (
        rem Try to detect GPU VRAM using PowerShell script
        set "VRAM_DETECTED=0"
        if exist "%ROOT%Tools\hybrid\get-gpu-vram.cmd" (
            for /f "usebackq delims=" %%A in (`"%ROOT%Tools\hybrid\get-gpu-vram.cmd" /format mb`) do (
                set "VRAM_DETECTED=%%A"
            )
        )
        if %VRAM_DETECTED% GTR 0 (
            rem Use 90% of detected VRAM as limit
            set /a "ASTROHYBRID_VRAM_LIMIT_MB=%VRAM_DETECTED% * 90 / 100"
            echo [Memory] Auto-detected GPU VRAM: %VRAM_DETECTED% MB, limit set to %ASTROHYBRID_VRAM_LIMIT_MB% MB (90%)
        ) else (
            rem Fallback to reasonable default
            set "ASTROHYBRID_VRAM_LIMIT_MB=6144"
            echo [Memory] VRAM detection failed, using default: %ASTROHYBRID_VRAM_LIMIT_MB% MB
        )
    )
    rem Enable texture streaming quality reduction under pressure
    set "ASTROHYBRID_TEXTURE_STREAMING_QUALITY=1"
    echo [Memory] Pressure handling enabled, VRAM limit: %ASTROHYBRID_VRAM_LIMIT_MB% MB
)

rem Draw-prep helper priority (A/B 2026-11-07 22:33-22:43, OVERNIGHT.md): with other programs running, a
rem helper that holds the CP's next draw was pre-empted and the CP spun 3-4 ms per frame; helpers at the
rem CP's priority (2) with short spins cut that to 0.4-0.7 ms and CP time by ~1.2 ms per frame.
rem ASTROHYBRID_DRAW_PREP_TUNING=0 restores the previous helper behaviour.
if not "%ASTROHYBRID_DRAW_PREP_TUNING%"=="0" (
  if not defined KYTY_DRAW_PREP_PRIORITY set KYTY_DRAW_PREP_PRIORITY=2
  if not defined KYTY_DRAW_PREP_SPIN_US set KYTY_DRAW_PREP_SPIN_US=10
  if not defined KYTY_DRAW_PREP_COLD_SPIN_US set KYTY_DRAW_PREP_COLD_SPIN_US=5
)
if not defined ASTROHYBRID_PRIORITY set ASTROHYBRID_PRIORITY=abovenormal
if not defined KYTY_GPU_WIDTH set KYTY_GPU_WIDTH=1920
if not defined KYTY_GPU_HEIGHT set KYTY_GPU_HEIGHT=1080
if not exist "%ROOT%Logs" mkdir "%ROOT%Logs"
set "ASTROHYBRID_EXTRA_ARGS="
if "%ASTROHYBRID_SKIP_INTRO%"=="1" set "ASTROHYBRID_EXTRA_ARGS=-skipIntro"
cd /d "%ROOT%Build\RunHybrid"
echo Logging to Logs\hybrid-run.out.log / Logs\hybrid-run.err.log
start "" /b /wait /%ASTROHYBRID_PRIORITY% "%ROOT%Build\RunHybrid\%ASTROHYBRID_EXE%" %ASTROHYBRID_EXTRA_ARGS% %* 1>"%ROOT%Logs\hybrid-run.out.log" 2>"%ROOT%Logs\hybrid-run.err.log"
set CODE=%ERRORLEVEL%
echo AstroBot.exe exited with %CODE%

rem Post-crash diagnostics collection (if game crashed)
if %CODE% NEQ 0 (
    echo [Post-Crash] Game exited with error code %CODE%, collecting diagnostics...
    
    rem Create crash report with timestamp
    set "CRASH_TIME=%DATE% %TIME%"
    set "CRASH_REPORT=%ROOT%Logs\crash-report-%DATE:~-4,4%%DATE:~-10,2%%DATE:~-7,2%-%TIME:~0,2%%TIME:~3,2%%TIME:~6,2%.txt"
    set "CRASH_REPORT=%CRASH_REPORT: =0%"
    set "CRASH_REPORT=%CRASH_REPORT:/=-%"
    set "CRASH_REPORT=%CRASH_REPORT::=-%"
    
    echo AstroHybrid Crash Report > "%CRASH_REPORT%"
    echo ======================== >> "%CRASH_REPORT%"
    echo Timestamp: %CRASH_TIME% >> "%CRASH_REPORT%"
    echo Exit Code: %CODE% >> "%CRASH_REPORT%"
    echo Executable: %ASTROHYBRID_EXE% >> "%CRASH_REPORT%"
    echo Priority: %ASTROHYBRID_PRIORITY% >> "%CRASH_REPORT%"
    echo Resolution: %KYTY_GPU_WIDTH%x%KYTY_GPU_HEIGHT% >> "%CRASH_REPORT%"
    echo. >> "%CRASH_REPORT%"
    
    rem Environment variables
    echo [Environment Variables] >> "%CRASH_REPORT%"
    set ASTROHYBRID >> "%CRASH_REPORT%" 2>nul
    set KYTY >> "%CRASH_REPORT%" 2>nul
    set APS5 >> "%CRASH_REPORT%" 2>nul
    echo. >> "%CRASH_REPORT%"
    
    rem Last 50 lines of stdout
    echo [Last 50 lines of stdout] >> "%CRASH_REPORT%"
    if exist "%ROOT%Logs\hybrid-run.out.log" (
        powershell -NoProfile -Command "Get-Content '%ROOT%Logs\hybrid-run.out.log' -Tail 50" >> "%CRASH_REPORT%" 2>nul
    )
    echo. >> "%CRASH_REPORT%"
    
    rem Last 50 lines of stderr
    echo [Last 50 lines of stderr] >> "%CRASH_REPORT%"
    if exist "%ROOT%Logs\hybrid-run.err.log" (
        powershell -NoProfile -Command "Get-Content '%ROOT%Logs\hybrid-run.err.log' -Tail 50" >> "%CRASH_REPORT%" 2>nul
    )
    echo. >> "%CRASH_REPORT%"
    
    rem System info if available
    if exist "%ROOT%Logs\system-info.json" (
        echo [System Info] >> "%CRASH_REPORT%"
        type "%ROOT%Logs\system-info.json" >> "%CRASH_REPORT%" 2>nul
        echo. >> "%CRASH_REPORT%"
    )
    
    echo [Post-Crash] Crash report saved to: %CRASH_REPORT%
    echo [Post-Crash] Please attach this file when reporting the issue on GitHub
)

exit /b %CODE%
