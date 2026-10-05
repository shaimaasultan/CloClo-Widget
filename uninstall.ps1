<#
.SYNOPSIS
  Uninstalls CloClo. install.ps1 copies this into the install folder and
  registers it as the uninstall command, so Settings > Apps > Installed apps
  > CloClo > Uninstall runs it - no project folder needed.

.DESCRIPTION
  Removes, for the copy in this script's own folder: the running widget,
  the Start Menu and desktop shortcuts and the "Start with Windows" entry
  (only those that point at this copy), the Installed apps entry and the
  folder itself. Settings and dictation files in AppData are left alone.

  A Program Files copy needs administrator rights to remove: after the
  per-user cleanup, this script runs itself again elevated (-Elevated) from
  its Program Files location, which only administrators can change.
#>
param([switch]$Elevated)

# First, before any command is used: load modules only from PowerShell's own
# system folder. Otherwise, when this runs elevated, commands would be looked
# up in the user's Documents\WindowsPowerShell\Modules first, where another
# program could plant a look-alike to run as administrator.
$env:PSModulePath = "$PSHOME\Modules"
$ErrorActionPreference = 'Stop'

$dir = $PSScriptRoot
$exe = Join-Path $dir 'CloCloWidget.exe'
$machineDir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'CloClo'
$isMachine = $dir.TrimEnd('\') -ieq $machineDir
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\CloClo'
$system32 = [Environment]::GetFolderPath('System')

function Stop-ThisCopy {
    Get-Process CloCloWidget -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -ieq $exe } |
        ForEach-Object { try { Stop-Process -Id $_.Id -Force -ErrorAction Stop; $_.WaitForExit(10000) | Out-Null } catch { } }
}

if ($Elevated) {
    Stop-ThisCopy   # also a copy that was started as administrator
    # Only from earlier versions, which wrote the entry for all users.
    Remove-Item -LiteralPath "HKLM:\$uninstallKey" -Recurse -Force -ErrorAction SilentlyContinue
    Set-Location -LiteralPath $system32   # never sit inside the folder being removed
    Remove-Item -LiteralPath $dir -Recurse -Force
    exit 0
}

# --- the user's side: only what points at this copy -------------------------
Stop-ThisCopy

$shell = New-Object -ComObject WScript.Shell
foreach ($link in (Join-Path ([Environment]::GetFolderPath('Programs')) 'CloClo.lnk'),
                  (Join-Path ([Environment]::GetFolderPath('Desktop')) 'CloClo.lnk')) {
    if ((Test-Path -LiteralPath $link) -and ($shell.CreateShortcut($link).TargetPath -ieq $exe)) {
        Remove-Item -LiteralPath $link -Force
    }
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$run = (Get-ItemProperty -Path $runKey -Name CloCloWidget -ErrorAction SilentlyContinue).CloCloWidget
if ($run -and $run.Trim().Trim('"') -ieq $exe) {
    Remove-ItemProperty -Path $runKey -Name CloCloWidget
}

# The Installed apps entry lives in the user's own registry - removed here,
# as the user, if it belongs to this copy.
$entry = Get-ItemProperty -LiteralPath "HKCU:\$uninstallKey" -ErrorAction SilentlyContinue
if ($entry -and $entry.InstallLocation.TrimEnd('\') -ieq $dir.TrimEnd('\')) {
    Remove-Item -LiteralPath "HKCU:\$uninstallKey" -Recurse -Force
}

if ($isMachine) {
    # Full path, never a bare "powershell.exe" (which PATH or the per-user
    # App Paths key could redirect), and the script from Program Files.
    $powershellExe = Join-Path $system32 'WindowsPowerShell\v1.0\powershell.exe'
    try {
        $p = Start-Process $powershellExe -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList `
            '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated'
    } catch {
        Write-Warning "Administrator rights are needed to remove CloClo from Program Files, and the request was declined."
        exit 1
    }
    exit $p.ExitCode
}

Set-Location -LiteralPath $system32
Remove-Item -LiteralPath $dir -Recurse -Force
exit 0
