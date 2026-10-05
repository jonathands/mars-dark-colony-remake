@echo off
rem 1280x960 window with the classic 640x480 picture at 2x.
rem Extra arguments are passed on, e.g. --single-player-war.
rem The preset keeps its own settings file, so Alt+Enter or the Video panel
rem here never changes the settings of run-debug.cmd.
call "%~dp0..\run-debug.cmd" --display-settings "%LOCALAPPDATA%\DarkColonyPort\presets\%~n0.json" --windowed --window-scale 2 --view classic --scale-mode integer %*
