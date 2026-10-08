<#
.SYNOPSIS
  Builds CloClo-Setup.exe: one signed installer, with CloClo inside, that
  installs it on any 64-bit Windows 10/11 PC - no .NET SDK or source needed
  there. Run it through Build-Setup.cmd.

.DESCRIPTION
  1. Publishes CloClo exactly as a Program Files install does (self-contained,
     single file, the native WPF DLLs beside it), signed.
  2. Builds CloClo.msi (installer\Package.wxs) around it and signs it.
  3. Builds CloClo-Setup.exe (installer\Bundle.wxs) around that and signs it.
  4. Copies the result to dist\CloClo-Setup-<version>.exe and prints its
     SHA-256, so whoever you send it to can check they got the same file.

  Signing goes through sign.ps1 for every file, with its rules: only a
  protected key may sign (Windows asks you to confirm each signing), RFC
  3161 timestamps, and each file is verified straight after. The signing
  certificate is -CertThumbprint, or else the newest protected CN=CloClo
  certificate in your store (the one Install-CloClo.cmd uses).

  -Unsigned builds without signing - for a quick local test only: an unsigned
  setup is "Unknown publisher" and isn't something to give to anyone.

  Self-signed CN=CloClo isn't trusted on other PCs, so Windows there shows
  the publisher as unknown and SmartScreen warns. For others to install
  without that, sign with a publicly trusted code-signing certificate
  (e.g. Azure Trusted Signing) and pass its thumbprint.
#>
param(
    [string]$CertThumbprint,
    [switch]$Unsigned
)
# Only Windows PowerShell's own modules, from its system folder - set before
# any command is used. (Plain concatenation, not Join-Path: Join-Path is
# itself a command that would be looked up in the user's module folder
# before this line takes effect.)
$env:PSModulePath = $PSHOME + '\Modules'
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
# Full paths, never bare names that PATH or App Paths could redirect.
$dotnetExe = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet\dotnet.exe'
$appDir = Join-Path $root 'obj\setup\app'
$distDir = Join-Path $root 'dist'

function Reset-SafeBuildDirectory([string]$path, [string]$expectedParent, [string]$projectRoot) {
    $fullPath = [IO.Path]::GetFullPath($path).TrimEnd('\')
    $fullParent = [IO.Path]::GetFullPath($expectedParent).TrimEnd('\')
    $fullRoot = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($fullPath).TrimEnd('\') -ine $fullParent -or
        -not $fullParent.StartsWith($fullRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a build directory outside the project: $fullPath"
    }

    # Check every existing component from the target through the project root,
    # then inspect descendants without following links.
    $current = $fullPath
    while ($current -and $current.Length -ge $fullRoot.Length) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (-not $item.PSIsContainer) { throw "Refusing to clean a path that isn't a directory: $current" }
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to clean a build path containing a junction or symbolic link: $current"
            }
        }
        if ($current -ieq $fullRoot) { break }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    if ($current -ine $fullRoot) { throw "Build directory isn't inside the project root: $fullPath" }

    if (Test-Path -LiteralPath $fullPath) {
        $pending = New-Object System.Collections.Generic.Stack[string]
        $pending.Push($fullPath)
        while ($pending.Count) {
            foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Refusing to clean build output containing a junction or symbolic link: $($item.FullName)"
                }
                if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            }
        }
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

try {
    if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator) -and
        (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue).EnableLUA -ne 0) {
        throw "Don't build in an administrator window: the build runs NuGet packages and build tasks, which shouldn't get administrator rights. Open a normal window."
    }
    if (-not (Test-Path -LiteralPath $dotnetExe)) {
        throw "The .NET SDK wasn't found at $dotnetExe. Install it from https://dotnet.microsoft.com/download."
    }

    # ------------------------------------------------------------ certificate
    if (-not $Unsigned -and -not $CertThumbprint) {
        $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
            Where-Object { $_.Subject -like 'CN=CloClo*' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
            Sort-Object NotBefore -Descending | Select-Object -First 1
        if (-not $cert) {
            throw "No CloClo signing certificate found. Run Install-CloClo.cmd once (it offers to create one), or pass -CertThumbprint."
        }
        $CertThumbprint = $cert.Thumbprint
    }
    if ($CertThumbprint -and $CertThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw "-CertThumbprint must be 40 hex characters." }
    if ($Unsigned) {
        $CertThumbprint = ''
        Write-Warning 'Building UNSIGNED - for a local test only, not for giving to anyone.'
    } else {
        Write-Host "Signing certificate: $CertThumbprint (Windows asks you to confirm each signing)"
    }

    # ------------------------------------------------------------ version
    # Windows Installer versions are major.minor.build with minor < 256 and
    # build < 65536: 1.<year>.<day of year x 150 + ten-minute slot> rises
    # with every build.
    $now = Get-Date
    $version = '1.{0}.{1}' -f ($now.Year - 2000), ($now.DayOfYear * 150 + [int][Math]::Floor(($now.Hour * 60 + $now.Minute) / 10))
    Write-Host "Setup version: $version"

    $signArg = if ($CertThumbprint) { "-p:SignCertThumbprint=$CertThumbprint" } else { '-p:SignCertThumbprint=' }
    $common = @('-c', 'Release', '-nologo', '--disable-build-servers', '-nodeReuse:false', '-v:minimal')

    # ------------------------------------------------------------ 1. CloClo
    Write-Host "`n1/3 Building CloClo..." -ForegroundColor Cyan
    Reset-SafeBuildDirectory $appDir ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($appDir))) $root
    & $dotnetExe publish (Join-Path $root 'CloCloWidget.csproj') @common -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=none -o $appDir $signArg `
        "-p:Version=$version"   # a rising file version, so Windows Installer replaces an older exe
    if ($LASTEXITCODE -ne 0) { throw "Building CloClo failed (exit $LASTEXITCODE)." }
    $exe = Join-Path $appDir 'CloCloWidget.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "The build left no CloCloWidget.exe in $appDir." }

    # ------------------------------------------------------------ 2. package
    Write-Host "`n2/3 Building CloClo.msi..." -ForegroundColor Cyan
    & $dotnetExe build (Join-Path $root 'installer\CloClo.Package.wixproj') @common "-p:AppDir=$appDir" "-p:SetupVersion=$version" $signArg
    if ($LASTEXITCODE -ne 0) { throw "Building the package failed (exit $LASTEXITCODE)." }
    $msi = Join-Path $root 'bin\installer\CloClo.Package\Release\CloClo.msi'

    # ------------------------------------------------------------ 3. setup
    Write-Host "`n3/3 Building CloClo-Setup.exe..." -ForegroundColor Cyan
    & $dotnetExe build (Join-Path $root 'installer\CloClo.Bundle.wixproj') @common "-p:MsiPath=$msi" "-p:SetupVersion=$version" $signArg
    if ($LASTEXITCODE -ne 0) { throw "Building the setup failed (exit $LASTEXITCODE)." }
    $setup = Join-Path $root 'bin\installer\CloClo.Bundle\Release\CloClo-Setup.exe'

    # ------------------------------------------------------------ result
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    $out = Join-Path $distDir "CloClo-Setup-$version.exe"
    Copy-Item -LiteralPath $setup -Destination $out -Force

    Write-Host ''
    foreach ($f in $exe, $msi, $out) {
        $sig = Get-AuthenticodeSignature -LiteralPath $f
        $who = if ($sig.SignerCertificate) { "$($sig.SignerCertificate.Subject) ($($sig.SignerCertificate.Thumbprint))" } else { 'not signed' }
        Write-Host ("{0,-28} {1,-12} {2}" -f (Split-Path $f -Leaf), $sig.Status, $who)

        if ($CertThumbprint) {
            if ($sig.Status -notin 'Valid', 'UnknownError', 'NotTrusted' -or
                -not $sig.SignerCertificate -or
                $sig.SignerCertificate.Thumbprint -ine $CertThumbprint) {
                throw "$($f.FullName) doesn't have an intact signature from the requested certificate $CertThumbprint (status: $($sig.Status))."
            }
        } elseif ($sig.Status -ne 'NotSigned') {
            throw "$($f.FullName) was expected to be unsigned, but its signature status is $($sig.Status)."
        }
    }
    Write-Host ''
    Write-Host "Setup: $out" -ForegroundColor Green
    Write-Host ("Size:  {0:0.0} MB" -f ((Get-Item -LiteralPath $out).Length / 1MB))
    Write-Host "SHA-256: $((Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash)"
    Write-Host 'Send the SHA-256 along with the file, so the person installing it can check they got the same file.'
    exit 0
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
