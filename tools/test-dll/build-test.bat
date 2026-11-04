@echo off
REM Quick toolchain test: builds a trivial 32-bit and 64-bit DLL with Zig.
REM Run this FIRST. If both DLLs appear, Zig is ready for the real hook.
setlocal
set ZIG=C:\tools\zig\zig-x86_64-windows-0.17.0\zig.exe
if not exist "%ZIG%" (
  echo [ERROR] zig.exe not found at %ZIG%
  echo Edit the ZIG= line to point to your zig.exe
  pause & exit /b 1
)
set SRC=%~dp0test.c
set OUT=%~dp0out
if not exist "%OUT%" mkdir "%OUT%"

echo Zig: "%ZIG%"
"%ZIG%" version
echo.

echo [1/2] Building 64-bit test DLL ...
"%ZIG%" cc -target x86_64-windows-gnu -shared -O2 -o "%OUT%\test64.dll" "%SRC%" -lkernel32
if errorlevel 1 goto fail

echo [2/2] Building 32-bit test DLL ...
"%ZIG%" cc -target x86-windows-gnu -shared -O2 -o "%OUT%\test32.dll" "%SRC%" -lkernel32
if errorlevel 1 goto fail

echo.
echo ===== SUCCESS =====
dir /b "%OUT%"
echo.
echo Both test DLLs built. Zig toolchain works.
pause & exit /b 0

:fail
echo.
echo ===== FAILED ===== build error above.
pause & exit /b 1
