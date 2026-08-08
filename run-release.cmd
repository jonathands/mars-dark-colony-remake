@echo off
setlocal

set "APP=%~dp0artifacts\DarkColony-win-x64\DarkColony.App.exe"
if not exist "%APP%" (
    echo Release executable not found: "%APP%"
    echo Build it first with: dotnet publish src\DarkColony.App -c Release -r win-x64 --self-contained true -o artifacts\DarkColony-win-x64
    exit /b 1
)

if not "%~1"=="" (
    "%APP%" --data "%~f1"
    exit /b %ERRORLEVEL%
)

if defined DARKCOLONY_DATA (
    "%APP%"
    exit /b %ERRORLEVEL%
)

set "DEFAULT_DATA=%~dp0..\Dark Colony"
if exist "%DEFAULT_DATA%\dc.exe" (
    "%APP%" --data "%DEFAULT_DATA%"
    exit /b %ERRORLEVEL%
)

echo Usage: %~nx0 "C:\path\to\Dark Colony"
echo.
echo Pass the root of an original installation containing dc.exe, gamestat,
echo sprites, and scenario. Assets remain external and are never copied here.
