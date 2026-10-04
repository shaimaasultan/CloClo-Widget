<#
.SYNOPSIS
  Builds, optionally signs, and installs CloClo for the current user.

.DESCRIPTION
  Publishes a self-contained single-file build and, when a certificate is
  given, signs it (via sign.ps1, see the csproj's signing targets): first
  every unsigned DLL that goes inside the exe - the app's own
  CloCloWidget.dll and any unsigned third-party library - then the
  finished exe itself. It then copies the exe to %LocalAppData%\Programs\CloCloWidget
  and creates a Start Menu shortcut (plus a desktop one with -DesktopShortcut).
  No admin rights needed. Re-running it upgrades in place.

.EXAMPLE
  .\install.ps1
.EXAMPLE
  .\install.ps1 -CertThumbprint 0123ABCD... -DesktopShortcut
.EXAMPLE
  .\install.ps1 -PfxPath C:\certs\codesign.pfx     # prompts for its password
.EXAMPLE
  .\install.ps1 -Uninstall
#>
param(
    [string]$CertThumbprint,
    [string]$PfxPath,
    [SecureString]$PfxPassword,
    [switch]$DesktopShortcut,
    [switch]$NoLaunch,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$appName    = 'CloClo'
$exeName    = 'CloCloWidget.exe'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\CloCloWidget'
$installExe = Join-Path $installDir $exeName
$startMenu  = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"
$desktop    = Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"
$runKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue   = 'CloCloWidget'   # same name StartupRegistration.cs uses

function Stop-RunningWidget {
    $running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Closing the running widget..."
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
}

if ($Uninstall) {
    Stop-RunningWidget
    Remove-Item $startMenu, $desktop -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Uninstalled. Settings and dictation files in AppData were left in place."
    return
}

# --- 1. build (and sign) ---------------------------------------------------
$publishDir = Join-Path $PSScriptRoot 'publish'
$publishArgs = @(
    'publish', (Join-Path $PSScriptRoot 'CloCloWidget.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-o', $publishDir, '-nologo'
)
$signing = [bool]($CertThumbprint -or $PfxPath)
if ($CertThumbprint) {
    $publishArgs += "-p:SignCertThumbprint=$CertThumbprint"
} elseif ($PfxPath) {
    $PfxPath = (Resolve-Path -LiteralPath $PfxPath).Path
    if (-not $PfxPassword) { $PfxPassword = Read-Host -AsSecureString "Password for $(Split-Path $PfxPath -Leaf) (Enter if none)" }
    $publishArgs += "-p:SignCertFile=$PfxPath"
    # Handed to sign.ps1 through the environment, never on a command line.
    $env:SIGN_CERT_PASSWORD = [Net.NetworkCredential]::new('', $PfxPassword).Password
} else {
    Write-Warning "No -CertThumbprint or -PfxPath given: the app will be unsigned (Windows SmartScreen may warn on first run)."
}

Write-Host "Building$(if ($signing) { ' and signing' })..."
try {
    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed (dotnet publish exited with $LASTEXITCODE)." }
} finally {
    Remove-Item Env:\SIGN_CERT_PASSWORD -ErrorAction SilentlyContinue
}

$builtExe = Join-Path $publishDir $exeName
if (-not (Test-Path $builtExe)) { throw "Build output not found at $builtExe." }

# --- 2. install ------------------------------------------------------------
Stop-RunningWidget
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item $builtExe $installExe -Force
Write-Host "Installed to $installExe"

# --- 3. shortcuts ----------------------------------------------------------
$shell = New-Object -ComObject WScript.Shell
$targets = @($startMenu)
if ($DesktopShortcut) { $targets += $desktop }
foreach ($path in $targets) {
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $installExe
    $link.WorkingDirectory = $installDir
    $link.IconLocation = "$installExe,0"
    $link.Description = 'CloClo desktop weather widget'
    $link.Save()
    Write-Host "Shortcut: $path"
}

# "Start with Windows" stores the exe path it was enabled from - if it's on,
# point it at the installed copy rather than wherever it used to run from.
if (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue) {
    Set-ItemProperty -Path $runKey -Name $runValue -Value "`"$installExe`""
    Write-Host "Updated 'Start with Windows' to the installed copy."
}

if ($signing) {
    $sig = Get-AuthenticodeSignature $installExe
    Write-Host "Signature: $($sig.Status) ($($sig.SignerCertificate.Subject))"
}

if (-not $NoLaunch) { Start-Process $installExe }
Write-Host "Done."
