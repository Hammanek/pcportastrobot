@echo off
rem GPU VRAM Detection for AstroHybrid
rem Returns VRAM in MB via environment variable or stdout

setlocal

set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%get-gpu-vram.ps1"

if not exist "%PS_SCRIPT%" (
    echo Error: PowerShell script not found: %PS_SCRIPT% >&2
    exit /b 1
)

rem Parse arguments
set "GPU_NAME="
set "OUTPUT_VAR="

:parse_args
if "%~1"=="" goto :run_script
if /i "%~1"=="/gpu" (
    shift
    set "GPU_NAME=%~1"
    shift
    goto :parse_args
)
if /i "%~1"=="/var" (
    shift
    set "OUTPUT_VAR=%~1"
    shift
    goto :parse_args
)
if /i "%~1"=="/format" (
    shift
    set "FORMAT=%~1"
    shift
    goto :parse_args
)
shift
goto :parse_args

:run_script
set "PS_ARGS="
if not "%GPU_NAME%"=="" set "PS_ARGS=%PS_ARGS% -GpuName "%GPU_NAME%""
if not "%FORMAT%"=="" set "PS_ARGS=%PS_ARGS% -OutputFormat %FORMAT%"

for /f "usebackq delims=" %%A in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %PS_ARGS%`) do (
    set "VRAM_RESULT=%%A"
)

if "%VRAM_RESULT%"=="" (
    echo Error: Failed to get VRAM >&2
    exit /b 1
)

if not "%OUTPUT_VAR%"=="" (
    set "%OUTPUT_VAR%=%VRAM_RESULT%"
) else (
    echo %VRAM_RESULT%
)

endlocal & set "VRAM_RESULT=%VRAM_RESULT%"
if not "%OUTPUT_VAR%"=="" set "%OUTPUT_VAR%=%VRAM_RESULT%"
exit /b 0