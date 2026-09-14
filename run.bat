@echo off
REM Launch the overlay. It starts hidden in the system tray; press your
REM shortcut (Ctrl+Shift+T by default) to bring it up over the game.
setlocal
cd /d "%~dp0"

set EXE=src\TarkovOverlay\bin\Release\net8.0-windows\win-x64\TLO.exe
if not exist "%EXE%" (
    echo Not built yet - running build.bat first.
    call build.bat || exit /b 1
)

start "" "%EXE%"
endlocal
