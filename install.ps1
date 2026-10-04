<#
.SYNOPSIS
  Builds, optionally signs, and installs CloClo - for the current user, or
  into Program Files with -ProgramFiles.

.DESCRIPTION
  Publishes a self-contained single-file build and, when a certificate is
  given, signs it (via sign.ps1, see the csproj's signing targets): first
  every unsigned DLL that goes inside the exe - the app's own
  CloCloWidget.dll and any unsigned third-party library - then the
  finished exe itself. It then copies the exe to %LocalAppData%\Programs\CloCloWidget
  and creates a Start Menu shortcut (plus a desktop one with -DesktopShortcut).
  No admin rights needed. Re-running it upgrades in place.

  -ProgramFiles installs to "C:\Program Files\CloClo" instead, which only
  administrators can change - so no other program running as you can swap
  the exe or its files. The build and signing still run as you; only the
  copy asks for administrator rights (one UAC prompt), and every copied file
  is checked against the build's SHA-256 afterwards. WPF's native DLLs are
  installed next to the exe there, rather than unpacked into %TEMP% at
  runtime. An existing per-user copy is removed.

.EXAMPLE
  .\install.ps1
.EXAMPLE
  .\install.ps1 -CertThumbprint 0123ABCD... -DesktopShortcut
.EXAMPLE
  .\install.ps1 -PfxPath C:\certs\codesign.pfx     # prompts for its password
.EXAMPLE
  .\install.ps1 -ProgramFiles -CertThumbprint 0123ABCD...
.EXAMPLE
  .\install.ps1 -Uninstall     # removes either kind of install
#>
param(
    [string]$CertThumbprint,
    [string]$PfxPath,
    [SecureString]$PfxPassword,
    [switch]$DesktopShortcut,
    [switch]$NoLaunch,
    [switch]$ProgramFiles,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

$appName    = 'CloClo'
$exeName    = 'CloCloWidget.exe'
# Known-folder lookups rather than environment variables, which any
# program running as this user can change.
$userDir    = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\CloCloWidget'
$machineDir = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'CloClo'
# Full paths, never a bare "powershell.exe": Windows resolves bare names via
# the per-user "App Paths" registry key and PATH, both of which another
# program running as this user could point at its own executable.
$powershellExe = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
$installDir = if ($ProgramFiles) { $machineDir } else { $userDir }
$installExe = Join-Path $installDir $exeName
$startMenu  = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"
$desktop    = Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"
$runKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue   = 'CloCloWidget'   # same name StartupRegistration.cs uses

function Stop-RunningWidget {
    $running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Closing the running widget..."
        try {
            $running | Stop-Process -Force -ErrorAction Stop
        } catch {
            # A copy started from an admin window runs elevated, and a
            # normal window isn't allowed to close it.
            throw "Couldn't close the running CloClo (it was probably started as administrator). Close it yourself - right-click it -> Exit - then run this again."
        }
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
}

function Test-Elevated {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Runs a script as administrator, always in a fresh PowerShell process.
# - The script travels inline (base64 on the command line), never as a file:
#   a script file in a folder this user can write to could be swapped by
#   another program just before it runs elevated.
# - Its first line limits module loading to PowerShell's own system folder.
#   Otherwise commands like Copy-Item or Get-FileHash are looked up in the
#   user's Documents\WindowsPowerShell\Modules first, where another program
#   could plant a look-alike module and have it run as administrator
#   (reproduced).
function Invoke-Elevated([string]$script) {
    $script = "`$env:PSModulePath = `"`$PSHOME\Modules`"`n`$ErrorActionPreference = 'Stop'`n" + $script
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $psArgs = '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded
    try {
        $p = if (Test-Elevated) {
            Start-Process $powershellExe -Wait -PassThru -WindowStyle Hidden -ArgumentList $psArgs
        } else {
            Start-Process $powershellExe -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList $psArgs
        }
    } catch {
        throw "Administrator rights are needed for Program Files, and the request was declined."
    }
    if ($p.ExitCode -ne 0) { throw "The administrator step failed (exit code $($p.ExitCode))." }
}

function Quote([string]$text) { "'" + $text.Replace("'", "''") + "'" }

if ($Uninstall) {
    Stop-RunningWidget
    Remove-Item $startMenu, $desktop -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue
    Remove-Item $userDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $machineDir) {
        Invoke-Elevated "Remove-Item -LiteralPath $(Quote $machineDir) -Recurse -Force"
    }
    Write-Host "Uninstalled. Settings and dictation files in AppData were left in place."
    return
}

# Building runs dotnet, NuGet packages and build tasks - none of which
# should ever run as administrator. The Program Files copy asks for those
# rights itself, for that one step only.
if (Test-Elevated) {
    throw "Run this from a normal PowerShell window, not one opened as administrator. With -ProgramFiles it asks for administrator rights itself, only for copying into Program Files."
}

# --- 1. build (and sign) ---------------------------------------------------
# Closed before building, not just before copying: a widget started from
# publish\ locks the exe the build is about to overwrite.
Stop-RunningWidget

# $PSScriptRoot is empty when the script's text is pasted into a console
# rather than run as a file - fall back to the current folder then.
$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
if (-not (Test-Path (Join-Path $root 'CloCloWidget.csproj'))) {
    throw "CloCloWidget.csproj not found in $root - run this from the cloclo-widget folder: .\install.ps1"
}
$publishDir = Join-Path $root 'publish'
# Emptied first: the two install kinds publish different file sets, and a
# stale DLL from the other kind must never be installed.
if (Test-Path $publishDir) { Get-ChildItem $publishDir -Force | Remove-Item -Recurse -Force }
# Per-user: WPF's native DLLs ride inside the exe and get unpacked to %TEMP%
# (verified at startup, see NativeLibraryCheck.cs). Program Files: they sit
# next to the exe instead, where only administrators can change them.
$selfExtract = if ($ProgramFiles) { 'false' } else { 'true' }
$publishArgs = @(
    'publish', (Join-Path $root 'CloCloWidget.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', "-p:IncludeNativeLibrariesForSelfExtract=$selfExtract",
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
# An update must come from the same signer as what's installed. A different
# certificate (or none, replacing a signed copy) is exactly what a swapped
# build would look like, so it needs an explicit YES - with both thumbprints
# shown - rather than going through quietly.
$installed = @((Join-Path $machineDir $exeName), (Join-Path $userDir $exeName)) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($installed) {
    $oldSigner = (Get-AuthenticodeSignature -LiteralPath $installed).SignerCertificate
    $newSigner = (Get-AuthenticodeSignature -LiteralPath $builtExe).SignerCertificate
    if ($oldSigner -and (-not $newSigner -or $newSigner.Thumbprint -ne $oldSigner.Thumbprint)) {
        Write-Warning "This build isn't signed by the same certificate as the installed CloClo."
        Write-Host "  Installed: $($oldSigner.Thumbprint)  $($oldSigner.Subject)"
        Write-Host "  New build: $(if ($newSigner) { "$($newSigner.Thumbprint)  $($newSigner.Subject)" } else { 'not signed' })"
        Write-Host "Continue only if you changed the certificate yourself."
        $answer = try { Read-Host "Type YES to replace it" } catch { '' }
        if ($answer -cne 'YES') { throw "Not installed - the installed copy was left as it was." }
    }
}

Stop-RunningWidget   # again, in case one was started while building
$files = Get-ChildItem $publishDir -File | Where-Object Extension -in '.exe', '.dll'
if ($ProgramFiles) {
    # Hashed here, as the user who built them, and re-checked by the
    # elevated copy - so what lands in Program Files is exactly the build.
    $entries = foreach ($f in $files) {
        "@{ Src = $(Quote $f.FullName); Hash = '$((Get-FileHash $f.FullName -Algorithm SHA256).Hash)' }"
    }
    Invoke-Elevated @"
`$dest = $(Quote $machineDir)
New-Item -ItemType Directory -Force -Path `$dest | Out-Null
Get-ChildItem `$dest -File | Remove-Item -Force
foreach (`$f in @($($entries -join ', '))) {
    `$target = Join-Path `$dest (Split-Path `$f.Src -Leaf)
    Copy-Item -LiteralPath `$f.Src -Destination `$target -Force
    if ((Get-FileHash -LiteralPath `$target -Algorithm SHA256).Hash -ne `$f.Hash) {
        Remove-Item -LiteralPath `$target -Force
        throw "`$target changed between build and install - not installed."
    }
}
"@
    # A per-user copy left behind would still be the one anything else
    # (old shortcuts, "Start with Windows") points at.
    if (Test-Path $userDir) {
        Remove-Item $userDir -Recurse -Force
        Write-Host "Removed the previous per-user copy."
    }
} else {
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    foreach ($f in $files) { Copy-Item $f.FullName (Join-Path $installDir $f.Name) -Force }
    if (Test-Path $machineDir) {
        Write-Warning "A Program Files copy also exists in $machineDir. Remove it with: .\install.ps1 -Uninstall"
    }
}
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
    $meaning = switch ($sig.Status) {
        'Valid' { 'signed and trusted' }
        # Get-AuthenticodeSignature's name for "intact signature, but the
        # certificate chains to a root this PC doesn't trust" - always the
        # case for a self-signed certificate until it's trusted.
        'UnknownError' { "signed, but this PC doesn't trust the certificate yet (normal for a self-signed one)" }
        default { "$($sig.Status) - $($sig.StatusMessage)" }
    }
    Write-Host "Signature: $meaning [$($sig.SignerCertificate.Subject)]"
}

# Never elevated here (checked above), so the widget starts as the normal
# user too.
if (-not $NoLaunch) { Start-Process $installExe }
Write-Host "Done."
