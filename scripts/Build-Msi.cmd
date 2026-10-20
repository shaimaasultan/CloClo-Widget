@echo off
rem ---------------------------------------------------------------------------
rem  Build-Msi.cmd - double-click to build dist\CloClo-<version>.msi
rem  and its matching .txt build log with its SHA-256. Same as Build-Setup.cmd,
rem  but the MSI alone, without the setup program around it.
rem  See build-setup.ps1 for the details.
rem ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-setup.ps1" -MsiOnly %*
set "buildExit=%errorlevel%"
if not "%buildExit%"=="0" (
    echo.
    echo CloClo MSI was NOT built. See the error above; dist was not updated.
) else (
    echo.
    echo CloClo MSI and matching build log were saved in dist.
)
echo.
pause
endlocal & exit /b %buildExit%
