@echo off
rem Exclusive fullscreen: the monitor switches to 640x480, as the original did.
rem Extra arguments are passed on, e.g. --single-player-war.
rem The preset keeps its own settings file, so Alt+Enter or the Video panel
rem here never changes the settings of run-debug.cmd.
call "%~dp0..\run-debug.cmd" --display-settings "%LOCALAPPDATA%\DarkColonyPort\presets\%~n0.json" --exclusive 640x480 --view classic --scale-mode integer %*
