@echo off
rem ---------------------------------------------------------------------------
rem  Check-Source.cmd - double-click to see whether anything in this project
rem  folder changed since CloClo was last installed.
rem
rem  It runs the checker and the source record that install.ps1 put into the
rem  INSTALL folder - for a Program Files install only administrators can
rem  change those, so whatever changed the code here can't also have changed
rem  the record. (The copies in this folder aren't used for the check.)
rem ---------------------------------------------------------------------------
setlocal
set "CHECKER=%ProgramFiles%\CloClo\check-source.ps1"
if exist "%CHECKER%" goto :run

set "CHECKER=%LOCALAPPDATA%\Programs\CloCloWidget\check-source.ps1"
if not exist "%CHECKER%" (
    echo CloClo isn't installed with a source record yet. Run Install-CloClo.cmd first.
    goto :done
)
echo Note: this is a per-user install - its source record sits in a folder
echo you can write to, so it's weaker than a Program Files install's.
echo.

:run
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%CHECKER%" -Root "%~dp0."

:done
echo.
pause
endlocal
