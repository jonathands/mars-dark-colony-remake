@echo off
rem Borderless fullscreen with the classic 640x480 picture at the largest whole scale.
rem Extra arguments are passed on, e.g. --single-player-war.
rem The preset keeps its own settings file, so Alt+Enter or the Video panel
rem here never changes the settings of run-debug.cmd.
call "%~dp0..\run-debug.cmd" --display-settings "%LOCALAPPDATA%\DarkColonyPort\presets\%~n0.json" --fullscreen --view classic --scale-mode integer %*
