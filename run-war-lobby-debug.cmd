@echo off
rem Debug build, straight to the Single Player War lobby.
call "%~dp0run-debug.cmd" --single-player-war %*
exit /b %ERRORLEVEL%
