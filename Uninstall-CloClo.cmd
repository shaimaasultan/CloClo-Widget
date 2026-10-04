@echo off
rem ---------------------------------------------------------------------------
rem  Uninstall-CloClo.cmd - double-click to remove CloClo (Program Files and/or
rem  per-user copy, shortcuts, "Start with Windows", the Installed apps entry).
rem  Settings and dictation files in AppData are kept. Asks for administrator
rem  rights only if a Program Files copy is installed.
rem  (Settings > Apps > Installed apps > CloClo > Uninstall does the same.)
rem ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0"
if not exist "%~dp0install.ps1" (
    echo install.ps1 wasn't found next to this file. Keep Uninstall-CloClo.cmd in the cloclo-widget folder.
    goto :done
)

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall
if errorlevel 1 (
    echo.
    echo CloClo was NOT fully removed - see the message above.
)

:done
echo.
pause
endlocal
