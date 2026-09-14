@echo off
REM Rebuild the overlay. Needs the .NET 8 (or newer) SDK on PATH.
setlocal
cd /d "%~dp0"

echo Building...
dotnet build "src\TarkovOverlay\TarkovOverlay.csproj" -c Release --nologo -v q
if errorlevel 1 (
    echo.
    echo BUILD FAILED.
    pause
    exit /b 1
)

echo.
echo Done.  Launch with run.bat
endlocal
