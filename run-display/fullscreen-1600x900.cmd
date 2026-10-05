@echo off
rem Borderless fullscreen with a 1600x900 gameplay view, fitted to the monitor (1.2x on 1920x1080).
rem Extra arguments are passed on, e.g. --single-player-war.
rem The preset keeps its own settings file, so Alt+Enter or the Video panel
rem here never changes the settings of run-debug.cmd.
call "%~dp0..\run-debug.cmd" --display-settings "%LOCALAPPDATA%\DarkColonyPort\presets\%~n0.json" --fullscreen --view 1600x900 --scale-mode fit %*
