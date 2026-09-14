@echo off
REM Re-slice the map images in \maps into the tile pyramid the viewer reads.
REM Run this after dropping a new map PNG into the maps folder.
setlocal
cd /d "%~dp0"
python tools\tile_maps.py %*
pause
endlocal
