@echo off
REM Builds Mapivo hook DLLs for every XInput name games import,
REM in both 32-bit and 64-bit, under one DLL folder (out\DLL\x86 and out\DLL\x64).
REM Each name uses its own .def so the export ordinals match the real system
REM DLL exactly; games that import xinput by ordinal then get the right function.
setlocal enabledelayedexpansion
set ZIG=C:\tools\zig\zig-x86_64-windows-0.17.0\zig.exe
if not exist "%ZIG%" (
  echo [ERROR] zig.exe not found at %ZIG%
  echo Edit the ZIG= line in this .bat to point to your zig.exe
  pause & exit /b 1
)
set SRC=%~dp0..\src\Mapivo.Hook\xinput_hook.c
set ROOT=%~dp0..\out\DLL
set NAMES=xinput1_1 xinput1_2 xinput1_3 xinput1_4 xinput9_1_0
set ZIG_LOCAL_CACHE_DIR=%~dp0.zig-cache
set ZIG_GLOBAL_CACHE_DIR=%~dp0.zig-global

if not exist "%ROOT%\x86" mkdir "%ROOT%\x86"
if not exist "%ROOT%\x64" mkdir "%ROOT%\x64"

call :build x86    "%ROOT%\x86"
call :build x86_64 "%ROOT%\x64"
goto done

:build
set ARCH=%~1
set DIR=%~2
for %%N in (%NAMES%) do (
  echo Building %%N.dll [%ARCH%] ...
  "%ZIG%" cc -target %ARCH%-windows-gnu -shared -O2 -o "%DIR%\%%N.dll" "%SRC%" "%~dp0%%N.def" -luser32 -lkernel32 -ldinput8 -ldxguid
  if errorlevel 1 goto fail
)
  echo Building dinput8.dll [%ARCH%] ...
  "%ZIG%" cc -target %ARCH%-windows-gnu -shared -O2 -o "%DIR%\dinput8.dll" "%~dp0..\src\Mapivo.Hook\dinput8_hook.c" "%~dp0dinput8.def" -lole32 -luser32 -lkernel32
  if errorlevel 1 goto fail
exit /b 0

:done
echo.
echo ===== SUCCESS =====
echo x86:
dir /b "%ROOT%\x86"
echo x64:
dir /b "%ROOT%\x64"
echo.
echo All XInput hook DLLs are under "%ROOT%"
pause & exit /b 0

:fail
echo.
echo [FAILED] build error above.
pause & exit /b 1
