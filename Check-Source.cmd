@echo off
rem ---------------------------------------------------------------------------
rem  Check-Source.cmd - double-click to see whether anything in this project
rem  folder changed since CloClo was last installed.
rem
rem  It runs the checker and the source record that install.ps1 put into the
rem  INSTALL folder - for a Program Files install only administrators can
rem  change those, so whatever changed the code here can't also have changed
rem  the record. (The copies in this folder aren't used for the check.)
rem
rem  The install folder is looked up with Windows' known-folder API inside
rem  PowerShell, not with %ProgramFiles% / %LOCALAPPDATA%: environment
rem  variables can be changed by any program running as you, which could then
rem  point this at a checker of its own.
rem ---------------------------------------------------------------------------
setlocal
rem The project folder goes in through the environment, so a folder name
rem with quotes or apostrophes can't break the command below.
set "CLOCLO_ROOT=%~dp0."

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -Command "$env:PSModulePath = Join-Path $PSHOME 'Modules'; $pf = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'CloClo\check-source.ps1'; $user = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\CloCloWidget\check-source.ps1'; $checker = @($pf, $user) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1; if (-not $checker) { Write-Host 'CloClo is not installed with a source record yet. Run Install-CloClo.cmd first.'; exit 2 }; if ($checker -ne $pf) { Write-Host 'Note: this is a per-user install - its source record sits in a folder you can write to, so the check is weaker than with a Program Files install.'; Write-Host '' }; & $checker -Root $env:CLOCLO_ROOT; exit $LASTEXITCODE"

echo.
pause
endlocal
