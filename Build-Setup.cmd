@echo off
rem ---------------------------------------------------------------------------
rem  Build-Setup.cmd - double-click to build dist\CloClo-Setup-<version>.exe:
rem  one signed installer, with CloClo inside, for this or any other PC.
rem  See build-setup.ps1 for the details.
rem ---------------------------------------------------------------------------
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-setup.ps1" %*
echo.
pause
endlocal
