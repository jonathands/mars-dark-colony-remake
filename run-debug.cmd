@echo off
setlocal
cd /d "%~dp0"

set "DARKCOLONY_GAME_DATA=%~dp0..\Dark Colony"
if not exist "%DARKCOLONY_GAME_DATA%\dc.exe" (
  echo Dark Colony installation not found:
  echo   %DARKCOLONY_GAME_DATA%
  echo.
  pause
  exit /b 1
)

dotnet run --project "src\DarkColony.App\DarkColony.App.csproj" --configuration Debug -- --data "%DARKCOLONY_GAME_DATA%" %*
set "DARKCOLONY_EXIT=%ERRORLEVEL%"

if not "%DARKCOLONY_EXIT%"=="0" (
  echo.
  echo Debug run failed with exit code %DARKCOLONY_EXIT%.
  pause
)

exit /b %DARKCOLONY_EXIT%
