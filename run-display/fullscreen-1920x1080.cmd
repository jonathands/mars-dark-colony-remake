@echo off
rem Borderless fullscreen with a 1920x1080 gameplay view (native on a 1920x1080 monitor).
rem Extra arguments are passed on, e.g. --single-player-war.
rem The preset keeps its own settings file, so Alt+Enter or the Video panel
rem here never changes the settings of run-debug.cmd.
call "%~dp0..\run-debug.cmd" --display-settings "%LOCALAPPDATA%\DarkColonyPort\presets\%~n0.json" --fullscreen --view 1920x1080 --scale-mode integer %*
