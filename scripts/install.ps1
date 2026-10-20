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
  A first install enables Start with Windows for the current user; later
  upgrades preserve the user's on/off choice.
  No admin rights needed. Re-running it upgrades in place.

  -ProgramFiles installs to "C:\Program Files\CloClo" instead, which only
  administrators can change - so no other program running as you can swap
  the exe or its files. The build and signing still run as you; only the
  copy asks for administrator rights (one UAC prompt), and every copied file
  is checked against the build's SHA-256 afterwards. WPF's native DLLs are
  installed next to the exe there, rather than unpacked into %TEMP% at
  runtime. An existing per-user copy is removed.

.EXAMPLE
  .\scripts\install.ps1
.EXAMPLE
  .\scripts\install.ps1 -CertThumbprint 0123ABCD... -DesktopShortcut
.EXAMPLE
  .\scripts\install.ps1 -ProgramFiles -CertThumbprint 0123ABCD...
.EXAMPLE
  .\scripts\install.ps1 -ProgramFiles -UseCloCloCertificate -DesktopShortcut
  # what Install-CloClo.cmd runs: signs with your newest protected CloClo
  # certificate, offering to create one if there is none
.EXAMPLE
  .\scripts\install.ps1 -Uninstall     # removes either kind of install
#>
param(
    [string]$CertThumbprint,
    [switch]$DesktopShortcut,
    [switch]$NoLaunch,
    [switch]$ProgramFiles,
    [switch]$UseCloCloCertificate,
    [switch]$Uninstall
)

# Only Windows PowerShell's own modules, from its system folder - set before
# any command is used. Otherwise commands are looked up in the user's
# Documents\WindowsPowerShell\Modules first, where another program could
# plant a look-alike: a fake Start-Process could change what the UAC prompt
# starts, a fake Get-FileHash could fake the hashes the administrator step
# and the source check rely on.
# (Plain string concatenation, not Join-Path: Join-Path is itself a command
# that would be looked up - in the user's module folder - before this line
# takes effect. Reproduced with a look-alike Join-Path.)
$env:PSModulePath = $PSHOME + '\Modules'
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
# Same for dotnet: the SDK's own installer puts it under Program Files, where
# only administrators can change it.
$dotnetExe = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet\dotnet.exe'
$installDir = if ($ProgramFiles) { $machineDir } else { $userDir }
$installExe = Join-Path $installDir $exeName
$startMenu  = Join-Path ([Environment]::GetFolderPath('Programs')) "$appName.lnk"
$desktop    = Join-Path ([Environment]::GetFolderPath('Desktop')) "$appName.lnk"
$runKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue   = 'CloCloWidget'   # same name StartupRegistration.cs uses
# The Installed apps entry (Settings > Apps), in the user's own registry
# (HKCU) for both install kinds. Earlier versions put a Program Files
# install's entry in HKLM; place.ps1 and the uninstallers remove that.
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\CloClo'

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

# Refuse to clean a path that resolves outside its intended parent or has a
# junction/symlink anywhere below the project/install root. This prevents a
# redirected build-output directory (or a link inside it) from making cleanup
# recurse into unrelated files.
function Assert-SafeOutputDirectory([string]$path, [string]$parent) {
    $fullPath = [IO.Path]::GetFullPath($path).TrimEnd('\')
    $fullParent = [IO.Path]::GetFullPath($parent).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($fullPath).TrimEnd('\') -ine $fullParent) {
        throw "Refusing to clean output outside its expected parent: $fullPath"
    }
    $current = $fullPath
    while ($current -and $current.Length -ge $fullParent.Length) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (-not $item.PSIsContainer) { throw "Refusing to clean a path that isn't a directory: $current" }
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to clean a directory that is a junction or symbolic link: $current"
            }
        }
        if ($current -ieq $fullParent) { break }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    if ($current -ine $fullParent) { throw "Output path isn't inside its expected parent: $fullPath" }
    if (Test-Path -LiteralPath $fullPath) {
        $pending = New-Object System.Collections.Generic.Stack[string]
        $pending.Push($fullPath)
        while ($pending.Count) {
            foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Refusing to clean output containing a junction or symbolic link: $($item.FullName)"
                }
                if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            }
        }
    }
}

function Reset-SafeOutputDirectory([string]$path, [string]$parent) {
    Assert-SafeOutputDirectory $path $parent
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}

function Test-Elevated {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Runs a script as administrator, always in a fresh PowerShell process.
# - The script travels inline on the command line, never as a file: a script
#   file in a folder this user can write to could be swapped by another
#   program just before it runs elevated.
# - It travels as plain, readable PowerShell (-Command), not base64, so the
#   UAC prompt's "Show more details" shows exactly what will run as
#   administrator: every source and destination path, every file's SHA-256
#   and the registry entry written. Its lines are joined with "; " into one
#   line, and it's quoted with ConvertTo-NativeArgument so Windows hands it
#   to PowerShell exactly as written.
# - Its first statement limits module loading to PowerShell's own system
#   folder. Otherwise commands like Copy-Item or Get-FileHash are looked up in
#   the user's Documents\WindowsPowerShell\Modules first, where another program
#   could plant a look-alike module and have it run as administrator
#   (reproduced).
function ConvertTo-NativeArgument([string]$text) {
    # Windows' command-line rules (CommandLineToArgvW): inside a quoted
    # argument, a run of backslashes is literal unless a double quote follows
    # it; then the backslashes are doubled and the quote is escaped as \".
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $backslashes = 0
    foreach ($ch in $text.ToCharArray()) {
        if ($ch -eq '\') { $backslashes++; continue }
        if ($ch -eq '"') {
            [void]$sb.Append('\' * (2 * $backslashes + 1)).Append('"')
        } else {
            [void]$sb.Append('\' * $backslashes).Append($ch)
        }
        $backslashes = 0
    }
    [void]$sb.Append('\' * (2 * $backslashes)).Append('"')
    $sb.ToString()
}

function Get-ElevatedCommandLine([string]$script) {
    # Plain concatenation, not Join-Path - see the note at the top of this file.
    $first = @('$env:PSModulePath = $PSHOME + ''\Modules''', '$ErrorActionPreference = ''Stop''')
    $lines = $first + @($script -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
    $argLine = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' + (ConvertTo-NativeArgument ($lines -join '; '))
    # Windows caps a command line at 32,767 characters, program path included.
    if ($argLine.Length -gt 30000) { throw "The administrator step is too long for one command line ($($argLine.Length) characters)." }
    $argLine
}

function Invoke-Elevated([string]$script) {
    $argLine = Get-ElevatedCommandLine $script
    try {
        $p = if (Test-Elevated) {
            Start-Process $powershellExe -Wait -PassThru -WindowStyle Hidden -ArgumentList $argLine
        } else {
            Start-Process $powershellExe -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList $argLine
        }
    } catch {
        # Only Windows' "cancelled by the user" (1223) means No was clicked
        # on the prompt; anything else is a real failure to start it, and
        # its own message says why.
        $e = $_.Exception
        while ($e -and -not ($e -is [System.ComponentModel.Win32Exception])) { $e = $e.InnerException }
        if ($e -and $e.NativeErrorCode -eq 1223) {
            throw "Administrator rights are needed for Program Files, and the request was declined. Nothing was changed."
        }
        throw "The administrator prompt couldn't be started: $($_.Exception.Message) Nothing was changed."
    }
    $p.ExitCode
}

function Quote([string]$text) { "'" + $text.Replace("'", "''") + "'" }

# PowerShell that writes the Installed apps entry under the given hive - HKCU,
# the user's own registry, for both install kinds; the administrator step
# writes no registry keys. Its uninstall command is the uninstall.ps1 copied
# into the install folder, run by PowerShell's full path.
function Get-UninstallEntryScript([string]$hive, [string]$version, [long]$bytes) {
    $key = "${hive}:\$uninstallKey"
    $strings = [ordered]@{
        DisplayName     = $appName
        DisplayVersion  = $version
        Publisher       = $appName
        DisplayIcon     = "$installExe,0"
        InstallLocation = $installDir
        UninstallString = "`"$powershellExe`" -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installDir 'uninstall.ps1')`""
        InstallDate     = (Get-Date -Format 'yyyyMMdd')
    }
    $dwords = [ordered]@{ NoModify = 1; NoRepair = 1; EstimatedSize = [int][Math]::Ceiling($bytes / 1KB) }
    $lines = @("New-Item -Path $(Quote $key) -Force | Out-Null")
    foreach ($name in $strings.Keys) {
        $lines += "New-ItemProperty -Path $(Quote $key) -Name '$name' -Value $(Quote $strings[$name]) -PropertyType String -Force | Out-Null"
    }
    foreach ($name in $dwords.Keys) {
        $lines += "New-ItemProperty -Path $(Quote $key) -Name '$name' -Value $($dwords[$name]) -PropertyType DWord -Force | Out-Null"
    }
    $lines -join "`n"
}

# A WiX setup has its own machine-wide Installed apps entry. Never copy a
# script install over its MSI-managed Program Files folder. Require the user
# to uninstall the setup first, as the setup installer does in the reverse
# direction. Registry command lines are never evaluated.
function Get-CloCloSetupEntries {
    $keys = @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
              'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')
    foreach ($parent in $keys) {
        foreach ($key in (Get-ChildItem -LiteralPath $parent -ErrorAction SilentlyContinue)) {
            $item = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            $codes = @($item.BundleUpgradeCode) | ForEach-Object { "$_".Trim('{}').ToUpperInvariant() }
            if ($item.DisplayName -eq 'CloClo' -and $item.Publisher -eq 'CloClo' -and
                $codes -contains 'EE052FAD-DFF9-49E0-8C68-CD517309BF13') {
                $item
            }
        }
    }
}

function Get-CloCloMsiEntries {
    foreach ($parent in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                          'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        foreach ($key in (Get-ChildItem -LiteralPath $parent -ErrorAction SilentlyContinue)) {
            $item = Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue
            if ($item.DisplayName -eq 'CloClo' -and $item.Publisher -eq 'CloClo' -and
                $item.WindowsInstaller -eq 1 -and $key.PSChildName -match '^\{[0-9A-Fa-f-]{36}\}$') {
                $key.PSChildName
            }
        }
    }
}

function Assert-NoExistingSetup {
    if (@(Get-CloCloSetupEntries).Count -or @(Get-CloCloMsiEntries).Count) {
        throw 'A setup-managed CloClo installation exists. Uninstall it from Settings > Apps before using the script installer.'
    }
}

if ($Uninstall) {
    if (@(Get-CloCloSetupEntries).Count -or @(Get-CloCloMsiEntries).Count) {
        throw 'This PC has a setup-managed CloClo install. Uninstall it from Settings > Apps; the script uninstaller cannot safely remove MSI-managed files.'
    }
    $userInstallParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($userDir))
    Assert-SafeOutputDirectory $userDir $userInstallParent
    # The source-tree uninstall removes both script install locations. Clear
    # startup before stopping the process or deleting shortcuts/files, but
    # only when this value points at one of those CloClo executables.
    $startup = (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue).$runValue
    $scriptExes = @((Join-Path $userDir $exeName), (Join-Path $machineDir $exeName))
    $ownedCommands = @($scriptExes | ForEach-Object { @($_, "`"$_`"", "`"$_`" --wait-for-security") })
    if ($startup -and $startup.Trim() -in $ownedCommands) {
        Remove-ItemProperty -Path $runKey -Name $runValue
    }
    if (-not (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue).$runValue) {
        $approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
        Remove-ItemProperty -Path $approvedKey -Name $runValue -ErrorAction SilentlyContinue
    }
    Stop-RunningWidget
    Remove-Item $startMenu, $desktop -Force -ErrorAction SilentlyContinue
    Remove-Item $userDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "HKCU:\$uninstallKey" -Recurse -Force -ErrorAction SilentlyContinue
    if ((Test-Path $machineDir) -or (Test-Path -LiteralPath "HKLM:\$uninstallKey")) {
        # The HKLM entry only exists from earlier versions.
        # Each removed only if it's there, without silencing errors: a real
        # failure (files in use) stops with an error, and success ends with
        # an explicit exit 0 - PowerShell -Command otherwise reports 1
        # whenever its last command failed, even an ignored one, such as
        # removing the HKLM entry that most installs never had.
        $code = Invoke-Elevated ("if (Test-Path -LiteralPath $(Quote $machineDir)) { Remove-Item -LiteralPath $(Quote $machineDir) -Recurse -Force }`n" +
            "if (Test-Path -LiteralPath $(Quote "HKLM:\$uninstallKey")) { Remove-Item -LiteralPath $(Quote "HKLM:\$uninstallKey") -Recurse -Force }`n" +
            "exit 0")
        if ($code -ne 0) { throw "Couldn't remove $machineDir (exit code $code) - is CloClo still running?" }
    }
    Write-Host "Uninstalled. Settings and dictation files in AppData were left in place."
    return
}

# Building runs dotnet, NuGet packages and build tasks - none of which
# should ever run as administrator. The Program Files copy asks for those
# rights itself, for that one step only.
# (With User Account Control switched off every window is an administrator
# one, so there's no other way to run it then.)
$uacOn = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue).EnableLUA -ne 0
if ((Test-Elevated) -and $uacOn) {
    throw "Run this from a normal PowerShell window, not one opened as administrator. With -ProgramFiles it asks for administrator rights itself, only for copying into Program Files."
}

# $PSScriptRoot is empty when the script's text is pasted into a console
# rather than run as a file - fall back to the current folder then.
$root = if ($PSScriptRoot) { [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..')) } else { (Get-Location).Path }
if (-not (Test-Path (Join-Path $root 'CloCloWidget.csproj'))) {
    throw "CloCloWidget.csproj not found in $root - run this from the cloclo-widget folder: .\scripts\install.ps1"
}
Assert-NoExistingSetup

# --- pre-check: has the source changed since the last install? ------------
# The installed copy carries a record of the source it was built from, and
# the checker that reads it - in Program Files only administrators can change
# either. Run that installed checker against this folder before building, so
# code nobody meant to change doesn't get built and signed unnoticed. Changes
# you made yourself (editing, git pull) just need a YES.
$installedChecker = @((Join-Path $machineDir 'check-source.ps1'), (Join-Path $userDir 'check-source.ps1')) |
    Where-Object { (Test-Path -LiteralPath $_) -and (Test-Path -LiteralPath (Join-Path (Split-Path $_) 'source-manifest.json')) } |
    Select-Object -First 1
if (-not $installedChecker) {
    Write-Host "No source record from an earlier install yet - this install will create one."
} else {
    & $installedChecker -Root $root -Quiet
    if ($LASTEXITCODE -eq 1) {
        & $installedChecker -Root $root | Out-Host
        if ([Console]::IsInputRedirected) {
            throw "The source changed since the last install, and this window can't ask whether to go on. Nothing was built or installed. Run it from a regular PowerShell window."
        }
        Write-Host "Type YES and press Enter to build and install these changes (anything else cancels): " -NoNewline -ForegroundColor Yellow
        $answer = try { Read-Host } catch { '' }
        if ($answer -cne 'YES') { throw "Nothing was built or installed." }
    } elseif ($LASTEXITCODE -eq 0) {
        Write-Host "Source unchanged since the last install."
    } else {
        Write-Warning "Couldn't compare the source with the last install's record (see above) - continuing; this install records a new one."
    }
}

# --- 0. find (or create) the signing certificate -----------------------------
# -UseCloCloCertificate: sign with the newest valid "CN=CloClo..." code-signing
# certificate in the user's store whose private key Windows protects - the
# same rule sign.ps1 enforces, checked here up front so an unprotected one is
# never even tried. With none, offer to create a protected one.
function Test-ProtectedKey($cert) {
    try {
        $key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
        if ($key -is [Security.Cryptography.RSACng]) {
            return $key.Key.UIPolicy.ProtectionLevel -ne [Security.Cryptography.CngUIProtectionLevels]::None -or
                $key.Key.Provider.Provider -eq 'Microsoft Smart Card Key Storage Provider'
        }
        if ($key -is [Security.Cryptography.RSACryptoServiceProvider]) {
            return $key.CspKeyContainerInfo.Protected -or $key.CspKeyContainerInfo.HardwareDevice
        }
    } catch { }
    $false
}

if ($UseCloCloCertificate -and -not $CertThumbprint) {
    $candidates = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -like 'CN=CloClo*' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotBefore -Descending)
    $usable = $candidates | Where-Object { Test-ProtectedKey $_ } | Select-Object -First 1
    if ($usable) {
        $CertThumbprint = $usable.Thumbprint
        Write-Host "Signing certificate: $($usable.Subject) ($CertThumbprint)"
    } else {
        foreach ($c in $candidates) {
            Write-Warning "$($c.Subject) ($($c.Thumbprint)) has no protected key, so it can't sign. Delete it in certmgr.msc > Personal > Certificates when you no longer need it."
        }
        if ([Console]::IsInputRedirected) {
            throw "No protected CloClo signing certificate found, and this window can't ask whether to create one. Run it from a regular PowerShell window."
        }
        Write-Host "No protected CloClo signing certificate was found."
        Write-Host "Create one now? Windows will show a dialog for the new key - Medium asks you to click OK at each signing, High asks for a password." -ForegroundColor Yellow
        Write-Host "Type Y and press Enter to create it (anything else cancels): " -NoNewline -ForegroundColor Yellow
        $answer = try { Read-Host } catch { '' }
        if ($answer -notin 'Y', 'y') { throw "Nothing was built or installed." }
        $created = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=CloClo' -CertStoreLocation Cert:\CurrentUser\My `
            -KeyProtection Protect -NotAfter (Get-Date).AddYears(5)
        if (-not (Test-ProtectedKey $created)) {
            throw "The new certificate's key didn't come out protected - nothing was built or installed."
        }
        $CertThumbprint = $created.Thumbprint
        Write-Host "Created signing certificate CN=CloClo ($CertThumbprint)."
    }
}

# --- 1. build (and sign) ---------------------------------------------------
# Closed before building, not just before copying: a widget started from
# publish\ locks the exe the build is about to overwrite.
Stop-RunningWidget

$publishDir = Join-Path $root 'publish'
# Emptied first: the two install kinds publish different file sets, and a
# stale DLL from the other kind must never be installed.
Reset-SafeOutputDirectory $publishDir $root
# Per-user: WPF's native DLLs ride inside the exe and get unpacked to %TEMP%
# (verified at startup, see NativeLibraryCheck.cs). Program Files: they sit
# next to the exe instead, where only administrators can change them.
$selfExtract = if ($ProgramFiles) { 'false' } else { 'true' }
$publishArgs = @(
    'publish', (Join-Path $root 'CloCloWidget.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', "-p:IncludeNativeLibrariesForSelfExtract=$selfExtract",
    '-o', $publishDir, '-nologo',
    # No build process outlives this install. MSBuild's worker nodes and the
    # C# compiler server otherwise keep running for minutes afterwards.
    '--disable-build-servers', '-nodeReuse:false'
)
$signing = [bool]$CertThumbprint
if ($CertThumbprint) {
    $publishArgs += "-p:SignCertThumbprint=$CertThumbprint"
} else {
    Write-Warning "No -CertThumbprint given: the app will be unsigned (Windows SmartScreen may warn on first run)."
}

# Record every source file's SHA-256 now, before building. It's checked
# again right after the build (so the record matches what was actually
# built) and then installed next to the program, where Check-Source.cmd
# compares the project folder against it later.
$checkSource = Join-Path $root 'scripts\check-source.ps1'
$sourceRecord = Join-Path $root 'obj\source-manifest.json'
New-Item -ItemType Directory -Force -Path (Split-Path $sourceRecord) | Out-Null
& $checkSource -Create $sourceRecord -Root $root -Quiet

Write-Host "Building$(if ($signing) { ' and signing' })..."
if (-not (Test-Path -LiteralPath $dotnetExe)) {
    throw "The .NET SDK wasn't found at $dotnetExe. Install it from https://dotnet.microsoft.com/download (it installs there)."
}
& $dotnetExe @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (dotnet publish exited with $LASTEXITCODE)." }

$builtExe = Join-Path $publishDir $exeName
if (-not (Test-Path $builtExe)) { throw "Build output not found at $builtExe." }
$builtModel = Join-Path $publishDir 'ggml-base.bin'
if (-not (Test-Path -LiteralPath $builtModel) -or
    (Get-FileHash -LiteralPath $builtModel -Algorithm SHA256).Hash -ne '60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE') {
    throw 'The bundled speech model is missing or altered in publish\. Check Git LFS and rebuild.'
}
$builtArabicModel = Join-Path $publishDir 'ggml-large-v3-turbo-q5_0.bin'
if (-not (Test-Path -LiteralPath $builtArabicModel) -or
    (Get-FileHash -LiteralPath $builtArabicModel -Algorithm SHA256).Hash -ne '394221709CD5AD1F40C46E6031CA61BCE88931E6E088C188294C6D5A55FFA7E2') {
    throw 'The Arabic speech model is missing or altered in publish\. Delete obj\whisper-models and rebuild.'
}
$builtLanguageModel = Join-Path $publishDir 'qwen2.5-1.5b-instruct-q4_k_m.gguf'
if (-not (Test-Path -LiteralPath $builtLanguageModel) -or
    (Get-FileHash -LiteralPath $builtLanguageModel -Algorithm SHA256).Hash -ne '6A1A2EB6D15622BF3C96857206351BA97E1AF16C30D7A74EE38970E434E9407E') {
    throw 'The language model is missing or altered in publish\. Delete obj\llm-models and rebuild.'
}
& $checkSource -Root $root -Manifest $sourceRecord -Quiet
if ($LASTEXITCODE -ne 0) {
    throw "Source files in $root changed while building, so the build can't be tied to a known state. Nothing was installed - run it again."
}

# --- 2. install ------------------------------------------------------------
$acceptThumbprint = ''
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
        # The question goes out through Write-Host: Read-Host's own prompt
        # text didn't show up when this script runs via "powershell -File",
        # leaving what looked like a hung window.
        # Started from PowerShell ISE (or with input piped in), this process
        # has no keyboard to read from and Read-Host would wait forever.
        if ([Console]::IsInputRedirected) {
            throw "Can't ask for YES here - this window can't pass keyboard input to the installer (PowerShell ISE does this). Nothing was installed. Run it from a regular PowerShell window (Start > Windows PowerShell), not ISE."
        }
        Write-Host "Type YES and press Enter to replace it (anything else cancels): " -NoNewline -ForegroundColor Yellow
        $answer = try { Read-Host } catch { '' }
        if ($answer -cne 'YES') { throw "Not installed - the installed copy was left as it was." }
        # Handed to place.ps1, which checks the signer again as administrator.
        $acceptThumbprint = if ($newSigner) { $newSigner.Thumbprint } else { 'unsigned' }
    }
}

# Quran recitations: each Assets\Recitations\<reciter>\ folder of SSSAAA.mp3
# files (git-ignored, downloaded by the person) becomes one code-free,
# signed pack in publish\Recitations\ - see pack-recitations.ps1. From here
# on it is just another built file: hashed, copied and checked like the rest.
$packArgs = @{ Root = $root; OutDir = $publishDir; DotnetExe = $dotnetExe }
if ($CertThumbprint) { $packArgs.Thumbprint = $CertThumbprint }
& (Join-Path $root 'scripts\pack-recitations.ps1') @packArgs | Out-Null

Stop-RunningWidget   # again, in case one was started while building
# Everything that gets installed is gathered in publish\ first: the build,
# the uninstaller (so Settings > Apps can remove CloClo without this project
# folder), and the source record with its checker (for Check-Source.cmd).
Copy-Item -LiteralPath (Join-Path $root 'scripts\uninstall.ps1'), $checkSource, $sourceRecord -Destination $publishDir -Force
$files = @(Get-ChildItem -LiteralPath $publishDir -File | Where-Object {
    $_.Extension -in '.exe', '.dll', '.ps1', '.json' -or $_.Name -in 'ggml-base.bin', 'ggml-large-v3-turbo-q5_0.bin', 'qwen2.5-1.5b-instruct-q4_k_m.gguf'
})
# The GPU speech runtime has the same file names as the CPU set, so it
# lives in gpu\. Every file carries its path relative to the install folder.
foreach ($f in $files) { $f | Add-Member -NotePropertyName Rel -NotePropertyValue $f.Name }
$gpuPublish = Join-Path $publishDir 'gpu'
if (Test-Path -LiteralPath $gpuPublish) {
    foreach ($f in @(Get-ChildItem -LiteralPath $gpuPublish -File -Filter '*.dll')) {
        $f | Add-Member -NotePropertyName Rel -NotePropertyValue ('gpu\' + $f.Name)
        $files += $f
    }
}
# The recitation packs live in Recitations\.
$recitationsPublish = Join-Path $publishDir 'Recitations'
if (Test-Path -LiteralPath $recitationsPublish) {
    foreach ($f in @(Get-ChildItem -LiteralPath $recitationsPublish -File -Filter 'CloClo.Recitations.*.dll')) {
        $f | Add-Member -NotePropertyName Rel -NotePropertyValue ('Recitations\' + $f.Name)
        $files += $f
    }
}
# Every file and the gpu\ folder, as paths relative to an install folder.
function Get-InstalledEntries([string]$dir) {
    @(Get-ChildItem -LiteralPath $dir -Force -Recurse | ForEach-Object {
        $rel = $_.FullName.Substring($dir.TrimEnd('\').Length + 1)
        if ($_.PSIsContainer) { if ($rel -notin 'gpu', 'Recitations') { "$rel\" } } else { $rel }
    })
}
$version = (Get-Item -LiteralPath $builtExe).VersionInfo.ProductVersion -replace '\+.*$', ''
$totalBytes = ($files | Measure-Object Length -Sum).Sum
if ($ProgramFiles) {
    # The SHA-256 of every file as built, taken here as the user who built
    # them. place.ps1 copies them into a staging folder in Program Files,
    # checks everything there and only then swaps it in (see place.ps1).
    $expected = ($files | ForEach-Object { "$($_.Rel)=$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }) -join ';'
    # What runs as administrator is a short bootstrap - which is all the UAC
    # prompt's "Show more details" shows: place.ps1's path and SHA-256, then
    # the data. As administrator it reads place.ps1's bytes once, checks them
    # against that SHA-256 and runs exactly those bytes from memory, so a
    # place.ps1 swapped at any moment after this point never runs (exit 8).
    $placeFile = Join-Path $root 'scripts\place.ps1'
    $placeHash = (Get-FileHash -LiteralPath $placeFile -Algorithm SHA256).Hash
    $bootstrap = @(
        "`$codeFile = $(Quote $placeFile)"
        "`$codeHash = '$placeHash'"
        '$bytes = [IO.File]::ReadAllBytes($codeFile)'
        'if (([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)) -replace ''-'', '''') -ne $codeHash) { exit 8 }'
        "`$Source = $(Quote $publishDir)"
        "`$Expected = $(Quote $expected)"
        "`$AcceptThumbprint = $(Quote $acceptThumbprint)"
        '. ([scriptblock]::Create([Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)))'
    ) -join "`n"
    Write-Host "Copying to $machineDir (Windows asks for administrator rights; 'Show more details' shows the code file, its SHA-256 and every file to copy)..."
    $code = Invoke-Elevated $bootstrap
    if ($code -ne 0) {
        $why = switch ($code) {
            2 { "the exe's signature is broken - it was changed after it was signed" }
            3 { "the build is signed by a different certificate than the installed copy" }
            4 { "a DLL isn't intact and signed by Microsoft" }
            5 { "a file in publish\ is a link or has an unexpected name" }
            7 { "a copied file differs from the build (was something changing publish\?)" }
            8 { "place.ps1 changed after the installer checked it" }
            9 { "Windows Firewall refused CloClo's rules (is it managed by another firewall or a group policy?)" }
            default { "the files couldn't be copied (is CloClo still running, maybe as administrator or in another account?)" }
        }
        throw "Not installed: $why. The installed copy was left as it was."
    }
    # A per-user copy left behind would still be the one anything else
    # (old shortcuts, "Start with Windows") points at.
    if (Test-Path $userDir) {
        Assert-SafeOutputDirectory $userDir ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($userDir)))
        Remove-Item $userDir -Recurse -Force
        Write-Host "Removed the previous per-user copy."
    }
} else {
    $installParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($userDir))
    $stageDir = $userDir + '.new'
    $backupDir = $userDir + '.old'
    Assert-SafeOutputDirectory $userDir $installParent
    Assert-SafeOutputDirectory $stageDir $installParent
    Assert-SafeOutputDirectory $backupDir $installParent

    # Recover a prior interrupted swap before preparing the next one.
    if (-not (Test-Path -LiteralPath $userDir) -and (Test-Path -LiteralPath $backupDir)) {
        Move-Item -LiteralPath $backupDir -Destination $userDir
    } elseif (Test-Path -LiteralPath $backupDir) {
        Remove-Item -LiteralPath $backupDir -Recurse -Force
    }

    Reset-SafeOutputDirectory $stageDir $installParent
    try {
        if (@($files | Where-Object { $_.Rel -like 'gpu\*' }).Count) { New-Item -ItemType Directory -Path (Join-Path $stageDir 'gpu') | Out-Null }
        if (@($files | Where-Object { $_.Rel -like 'Recitations\*' }).Count) { New-Item -ItemType Directory -Path (Join-Path $stageDir 'Recitations') | Out-Null }
        foreach ($f in $files) { Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $stageDir $f.Rel) }
        $stagedEntries = Get-InstalledEntries $stageDir
        if ($stagedEntries.Count -ne $files.Count -or @($stagedEntries | Where-Object { $files.Rel -notcontains $_ }).Count) {
            throw 'The staged per-user install has an unexpected file set.'
        }
        foreach ($f in $files) {
            $staged = Join-Path $stageDir $f.Rel
            if (-not (Test-Path -LiteralPath $staged -PathType Leaf) -or
                (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash) {
                throw "The staged per-user file '$($f.Rel)' doesn't match the build."
            }
        }
    } catch {
        Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }

    $movedOldInstall = $false
    if (Test-Path -LiteralPath $userDir) {
        Move-Item -LiteralPath $userDir -Destination $backupDir
        $movedOldInstall = $true
    }
    try {
        Move-Item -LiteralPath $stageDir -Destination $userDir
        $installedEntries = Get-InstalledEntries $userDir
        if ($installedEntries.Count -ne $files.Count -or @($installedEntries | Where-Object { $files.Rel -notcontains $_ }).Count) {
            throw 'The installed per-user folder has an unexpected file set.'
        }
        foreach ($f in $files) {
            $installedFile = Join-Path $userDir $f.Rel
            if (-not (Test-Path -LiteralPath $installedFile -PathType Leaf) -or
                (Get-FileHash -LiteralPath $installedFile -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash) {
                throw "The installed per-user file '$($f.Rel)' doesn't match the build."
            }
        }
    } catch {
        if (Test-Path -LiteralPath $userDir) {
            Assert-SafeOutputDirectory $userDir $installParent
            Remove-Item -LiteralPath $userDir -Recurse -Force
        }
        if ($movedOldInstall -and (Test-Path -LiteralPath $backupDir)) {
            Move-Item -LiteralPath $backupDir -Destination $userDir
        }
        throw
    }
    if (Test-Path -LiteralPath $backupDir) {
        try { Remove-Item -LiteralPath $backupDir -Recurse -Force }
        catch { Write-Warning "The new install is active, but the old backup remains at ${backupDir}: $($_.Exception.Message)" }
    }
    if (Test-Path $machineDir) {
        Write-Warning "A Program Files copy also exists in $machineDir. Remove it with: .\scripts\install.ps1 -Uninstall"
    }
}

# Check from this (normal) side too that the install folder now holds
# exactly the files that were built - nothing missing, changed or extra.
$present = Get-InstalledEntries $installDir
$differs = @($files | Where-Object {
    $inst = Join-Path $installDir $_.Rel
    -not (Test-Path -LiteralPath $inst) -or (Get-FileHash -LiteralPath $inst -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
} | ForEach-Object Rel) + @($present | Where-Object { $files.Rel -notcontains $_ })
if ($differs.Count) { throw "Install verification failed in $installDir; files differ from the build: $($differs -join ', ')" }
else { Write-Host "Checked: $installDir holds exactly the $($files.Count) files that were built." }

# The Installed apps entry, in the user's own registry for both install
# kinds (an older all-users entry is removed by place.ps1).
& ([scriptblock]::Create((Get-UninstallEntryScript 'HKCU' $version $totalBytes)))
Write-Host "Installed to $installExe"
Write-Host "Listed in Settings > Apps > Installed apps as '$appName' (uninstall it from there)."

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

# "Start with Windows" stores the exe path it was enabled from. A first
# install enables it for this user; an upgrade preserves a prior opt-out.
# When enabled, point it at the installed copy rather than wherever it used
# to run from.
# Read back afterwards: an entry left pointing at a removed per-user copy
# means CloClo won't start, and that folder is writable by any program
# running as this user.
if ((Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue) -or -not $installed) {
    $wanted = "`"$installExe`" --wait-for-security"
    New-Item -Path $runKey -Force | Out-Null
    New-ItemProperty -Path $runKey -Name $runValue -Value $wanted -PropertyType String -Force | Out-Null
    $now = (Get-ItemProperty -Path $runKey -Name $runValue).$runValue
    if ($now -eq $wanted) {
        Write-Host "Enabled 'Start with Windows' for this user at the installed copy."
    } else {
        Write-Warning "Couldn't update 'Start with Windows' (it still points to $now). Turn it off and on again from CloClo's right-click menu."
    }
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
