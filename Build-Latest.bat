@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
set "buildExitCode=%ERRORLEVEL%"
if not "%buildExitCode%"=="0" goto failed

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -LocalOnly
set "buildExitCode=%ERRORLEVEL%"
if not "%buildExitCode%"=="0" goto failed

echo.
echo Release-Build, Tests und Export erfolgreich.
echo EXE: "%~dp0artifacts\win-x64\DLSSManager.App.exe"
goto done

:failed
echo.
echo Build, Tests oder Export fehlgeschlagen. Exitcode: %buildExitCode%

:done
pause
exit /b %buildExitCode%
