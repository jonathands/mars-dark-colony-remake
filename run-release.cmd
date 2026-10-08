@echo off
setlocal

set "APP=%~dp0artifacts\DarkColony-win-x64\DarkColony.App.exe"
if not exist "%APP%" (
    echo Release executable not found: "%APP%"
    echo Build it first with: dotnet publish src\DarkColony.App -c Release -r win-x64 --self-contained true -o artifacts\DarkColony-win-x64
    exit /b 1
)

rem Opens in borderless fullscreen with a 1920x1080 gameplay view (native on a
rem 1920x1080 monitor), until the Video panel or Alt+Enter saves other settings.
rem They are saved to a settings file of its own, so they never change the
rem display.json of run-debug.cmd. Delete that file to get 1920x1080 back.
set "SETTINGS=%LOCALAPPDATA%\DarkColonyPort\presets\%~n0.json"
set "DISPLAY_FLAGS=--display-settings "%SETTINGS%""
if not exist "%SETTINGS%" set "DISPLAY_FLAGS=%DISPLAY_FLAGS% --fullscreen --view 1920x1080 --scale-mode integer"

if not "%~1"=="" (
    "%APP%" --data "%~f1" %DISPLAY_FLAGS%
    exit /b %ERRORLEVEL%
)

if defined DARKCOLONY_DATA (
    "%APP%" %DISPLAY_FLAGS%
    exit /b %ERRORLEVEL%
)

set "DEFAULT_DATA=%~dp0..\Dark Colony"
if exist "%DEFAULT_DATA%\dc.exe" (
    "%APP%" --data "%DEFAULT_DATA%" %DISPLAY_FLAGS%
    exit /b %ERRORLEVEL%
)

echo Usage: %~nx0 "C:\path\to\Dark Colony"
echo.
echo Pass the root of an original installation containing dc.exe, gamestat,
echo sprites, and scenario. Assets remain external and are never copied here.
