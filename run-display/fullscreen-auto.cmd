@echo off
rem Borderless fullscreen with a view of the monitor divided by the largest whole scale (960x540 at 2x on 1920x1080).
rem Extra arguments are passed on, e.g. --single-player-war.
rem The preset keeps its own settings file, so Alt+Enter or the Video panel
rem here never changes the settings of run-debug.cmd.
call "%~dp0..\run-debug.cmd" --display-settings "%LOCALAPPDATA%\DarkColonyPort\presets\%~n0.json" --fullscreen --view auto --scale-mode integer %*
