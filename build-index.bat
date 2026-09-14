@echo off
REM Build the icon index the item scanner matches against.
REM
REM Reads a folder of "<tarkov.dev item id>.png" grid images and writes
REM data\icons.idx (~2 MB). The icons themselves are not needed at runtime.
setlocal
cd /d "%~dp0"
python tools\build_icon_index.py %*
pause
endlocal
