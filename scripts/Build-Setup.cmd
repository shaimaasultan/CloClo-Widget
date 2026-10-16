@echo off
rem ---------------------------------------------------------------------------
rem  Build-Setup.cmd - double-click to build dist\CloClo-<version>.msix
rem  and its matching .txt build log with the package's SHA-256.
rem  The package is signed with a publicly trusted certificate (Artifact
rem  Signing by default), so it installs for one user without any certificate
rem  import. See build-msix.ps1 for the details. The old MSI setup is
rem  build-msi-legacy.ps1.
rem ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-msix.ps1" %*
set "buildExit=%errorlevel%"
if not "%buildExit%"=="0" (
    echo.
    echo CloClo MSIX was NOT built. See the error above; dist was not updated.
) else (
    echo.
    echo CloClo MSIX and matching build log were saved in dist.
)
echo.
pause
endlocal & exit /b %buildExit%
