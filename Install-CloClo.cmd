@echo off
rem ---------------------------------------------------------------------------
rem  Install-CloClo.cmd - double-click to build, sign and install CloClo the
rem  recommended way:
rem    - into C:\Program Files\CloClo (one administrator prompt, for the copy
rem      only), so no other program running as you can change it
rem    - signed with your newest protected CloClo certificate; if you have
rem      none, it offers to create one (an unprotected one is never used)
rem    - with Start Menu and desktop shortcuts, listed in Installed apps
rem  Run it as yourself - NOT with "Run as administrator"; install.ps1 asks
rem  for administrator rights itself, only when it copies into Program Files.
rem  Extra options are passed on to install.ps1, e.g.  Install-CloClo.cmd -NoLaunch
rem ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0"
if not exist "%~dp0install.ps1" (
    echo install.ps1 wasn't found next to this file. Keep Install-CloClo.cmd in the cloclo-widget folder.
    goto :done
)

rem PowerShell by its full path, never by name through PATH. The execution
rem policy bypass applies to this one run only and changes no setting.
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -ProgramFiles -UseCloCloCertificate -DesktopShortcut %*
if errorlevel 1 (
    echo.
    echo CloClo was NOT installed - see the message above.
) else (
    echo.
    echo CloClo is installed.
)

:done
echo.
pause
endlocal
