@echo off
rem ---------------------------------------------------------------------------
rem  Build-Setup.cmd - double-click to build dist\CloClo-Setup-<version>.exe
rem  and its matching .txt build log with the installer's SHA-256.
rem  See build-setup.ps1 for the details.
rem ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-setup.ps1" %*
set "buildExit=%errorlevel%"
if not "%buildExit%"=="0" (
    echo.
    echo CloClo setup was NOT built. See the error above; dist was not updated.
) else (
    echo.
    echo CloClo setup and matching build log were saved in dist.
)
echo.
pause
endlocal & exit /b %buildExit%
