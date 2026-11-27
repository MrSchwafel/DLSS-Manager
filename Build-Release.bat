@echo off
setlocal
set "releaseVersion=%~1"

if "%releaseVersion%"=="" (
    set "CSPROJ_PATH=%~dp0src\DLSSManager.App\DLSSManager.App.csproj"
    for /f "usebackq delims=" %%i in (`powershell.exe -NoProfile -Command "([xml](Get-Content -LiteralPath $env:CSPROJ_PATH -Raw)).SelectSingleNode('/Project/PropertyGroup/Version').InnerText"`) do set "defaultVersion=%%i"
    if defined defaultVersion (
        set /p "releaseVersion=Release-Version [Enter fuer %defaultVersion%]: "
        if "%releaseVersion%"=="" set "releaseVersion=%defaultVersion%"
    ) else (
        set /p "releaseVersion=Release-Version (z.B. 1.0.1): "
    )
)

if "%releaseVersion%"=="" (
    echo Keine Version angegeben.
    set "buildExitCode=1"
    goto failed
)

echo.
echo Erstelle Release %releaseVersion% ...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
set "buildExitCode=%ERRORLEVEL%"
if not "%buildExitCode%"=="0" goto failed

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Version "%releaseVersion%"
set "buildExitCode=%ERRORLEVEL%"
if not "%buildExitCode%"=="0" goto failed

set "packageZip=%~dp0artifacts\DLSS-Manager-CSharp-win-x64-%releaseVersion%.zip"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-release.ps1" -Package "%packageZip%"
set "buildExitCode=%ERRORLEVEL%"
if not "%buildExitCode%"=="0" goto failed

echo.
echo Release-Build, Paket und SHA-256 erfolgreich erstellt und geprueft.
echo EXE:     "%~dp0artifacts\win-x64\DLSSManager.App.exe"
echo ZIP:     "%packageZip%"
echo SHA-256: "%packageZip%.sha256"
goto done

:failed
echo.
echo Release-Build fehlgeschlagen. Exitcode: %buildExitCode%

:done
pause
exit /b %buildExitCode%
