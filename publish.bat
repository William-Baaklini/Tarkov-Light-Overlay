@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\publish.ps1" %*
if errorlevel 1 (
    echo Release packaging failed.
    exit /b 1
)
endlocal
