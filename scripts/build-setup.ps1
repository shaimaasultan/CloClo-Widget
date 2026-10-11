<#
.SYNOPSIS
  Builds CloClo-Setup.exe: one signed installer, with CloClo inside, that
  installs it on any 64-bit Windows 10/11 PC - no .NET SDK or source needed
  there. Run it through Build-Setup.cmd.

.DESCRIPTION
  1. Publishes CloClo exactly as a Program Files install does (self-contained,
     single-file exe, native DLLs and the local speech model beside it), signed.
  2. Builds CloClo.msi (installer\Package.wxs) around it and signs it.
  3. Builds CloClo-Setup.exe (installer\Bundle.wxs) around that and signs it.
  4. Copies the result to dist\CloClo-Setup-<version>.exe, with a matching
     .txt build log containing that exact file's SHA-256.

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

$root = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..'))
# Full paths, never bare names that PATH or App Paths could redirect.
$dotnetExe = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet\dotnet.exe'
$appDir = Join-Path $root 'obj\setup\app'
$logDir = Join-Path $root 'obj\setup\logs'
$distDir = Join-Path $root 'dist'
$script:logWriter = $null
$createdOut = $false
$createdLog = $false
$tempOut = $null

function Write-BuildMessage([string]$message, [ConsoleColor]$color = [ConsoleColor]::Gray) {
    Write-Host $message -ForegroundColor $color
    if ($script:logWriter) {
        $script:logWriter.WriteLine($message)
        $script:logWriter.Flush()
    }
}

function Invoke-DotNetLogged([string[]]$arguments) {
    # Capture emitted build output only. A PowerShell transcript could also
    # record console input; the protected-key approval stays in Windows UI.
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $dotnetExe @arguments 2>&1 | ForEach-Object {
            $line = [string]$_
            Write-Host $line
            $script:logWriter.WriteLine($line)
        }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $oldPreference
        $script:logWriter.Flush()
    }
    return $code
}

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
    }

    # ------------------------------------------------------------ version
    # Windows Installer versions are major.minor.build with minor < 256 and
    # build < 65536. Start at the current ten-minute slot, then advance past
    # any existing installer or log so a later build never reuses that pair.
    $now = Get-Date
    $minor = $now.Year - 2000
    $buildNumber = $now.DayOfYear * 150 + [int][Math]::Floor(($now.Hour * 60 + $now.Minute) / 10)
    if (Test-Path -LiteralPath $distDir) {
        $distItem = Get-Item -LiteralPath $distDir -Force
        if (-not $distItem.PSIsContainer -or ($distItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to publish to a non-directory or linked dist path: $distDir"
        }
    }
    do {
        if ($buildNumber -gt 65535) { throw 'No setup version remains for this year.' }
        $version = '1.{0}.{1}' -f $minor, $buildNumber
        $out = Join-Path $distDir "CloClo-Setup-$version.exe"
        $outLog = Join-Path $distDir "CloClo-Setup-$version.txt"
        $buildNumber++
    } while ((Test-Path -LiteralPath $out) -or (Test-Path -LiteralPath $outLog))

    Reset-SafeBuildDirectory $logDir ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($logDir))) $root
    $buildLog = Join-Path $logDir "CloClo-Setup-$version.txt"
    $script:logWriter = [IO.StreamWriter]::new($buildLog, $false, [Text.UTF8Encoding]::new($false))
    $script:logWriter.AutoFlush = $true
    if ($Unsigned) {
        Write-BuildMessage 'WARNING: Building UNSIGNED - for a local test only, not for giving to anyone.' Yellow
    } else {
        Write-BuildMessage "Signing certificate: $CertThumbprint (Windows asks you to confirm each signing)"
    }
    Write-BuildMessage "Setup version: $version"

    $signArg = if ($CertThumbprint) { "-p:SignCertThumbprint=$CertThumbprint" } else { '-p:SignCertThumbprint=' }
    $common = @('-c', 'Release', '-nologo', '--disable-build-servers', '-nodeReuse:false', '-v:minimal')

    # ------------------------------------------------------------ 1. CloClo
    Write-BuildMessage "`n1/3 Building CloClo..." Cyan
    Reset-SafeBuildDirectory $appDir ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($appDir))) $root
    $publishArgs = @('publish', (Join-Path $root 'CloCloWidget.csproj')) + $common + @(
        '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=false', '-p:DebugType=none',
        '-o', $appDir, $signArg, "-p:Version=$version")
    $exitCode = Invoke-DotNetLogged $publishArgs
    if ($exitCode -ne 0) { throw "Building CloClo failed (exit $exitCode)." }
    $exe = Join-Path $appDir 'CloCloWidget.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw "The build left no CloCloWidget.exe in $appDir." }

    # ------------------------------------------------------------ 2. package
    Write-BuildMessage "`n2/3 Building CloClo.msi..." Cyan
    $packageArgs = @('build', (Join-Path $root 'installer\CloClo.Package.wixproj')) + $common + @("-p:AppDir=$appDir", "-p:SetupVersion=$version", $signArg)
    $exitCode = Invoke-DotNetLogged $packageArgs
    if ($exitCode -ne 0) { throw "Building the package failed (exit $exitCode)." }
    $msi = Join-Path $root 'bin\installer\CloClo.Package\Release\CloClo.msi'
    if (-not (Test-Path -LiteralPath $msi -PathType Leaf)) { throw "The package build left no CloClo.msi at $msi." }

    # ------------------------------------------------------------ 3. setup
    Write-BuildMessage "`n3/3 Building CloClo-Setup.exe..." Cyan
    $bundleArgs = @('build', (Join-Path $root 'installer\CloClo.Bundle.wixproj')) + $common + @("-p:MsiPath=$msi", "-p:SetupVersion=$version", $signArg)
    $exitCode = Invoke-DotNetLogged $bundleArgs
    if ($exitCode -ne 0) { throw "Building the setup failed (exit $exitCode)." }
    $setup = Join-Path $root 'bin\installer\CloClo.Bundle\Release\CloClo-Setup.exe'
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "The setup build left no CloClo-Setup.exe at $setup." }

    # Verify all three artifacts before publishing anything to dist\.
    Write-BuildMessage ''
    foreach ($f in $exe, $msi, $setup) {
        $sig = Get-AuthenticodeSignature -LiteralPath $f
        $who = if ($sig.SignerCertificate) { "$($sig.SignerCertificate.Subject) ($($sig.SignerCertificate.Thumbprint))" } else { 'not signed' }
        Write-BuildMessage ("{0,-28} {1,-12} {2}" -f (Split-Path $f -Leaf), $sig.Status, $who)

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

    # ------------------------------------------------------------ result
    if (Test-Path -LiteralPath $distDir) {
        $distItem = Get-Item -LiteralPath $distDir -Force
        if (-not $distItem.PSIsContainer -or ($distItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to publish to a non-directory or linked dist path: $distDir"
        }
    } else {
        New-Item -ItemType Directory -Path $distDir | Out-Null
    }
    if ((Test-Path -LiteralPath $out) -or (Test-Path -LiteralPath $outLog)) {
        throw "The selected setup version already exists in dist; nothing was overwritten: $version"
    }
    $setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    Write-BuildMessage ''
    Write-BuildMessage "Setup: $out" Green
    Write-BuildMessage ("Size:  {0:0.0} MB" -f ((Get-Item -LiteralPath $setup).Length / 1MB))
    Write-BuildMessage "SHA-256: $setupHash"
    Write-BuildMessage "Build log: $outLog"
    Write-BuildMessage 'Send the SHA-256 along with the file, so the person installing it can check they got the same file.'
    $script:logWriter.Dispose()
    $script:logWriter = $null
    # Publish the log first. The EXE is copied to a temporary name, checked,
    # then renamed last; an interrupted build never leaves a final EXE without
    # its matching hash log.
    $outLogStream = [IO.File]::Open($outLog, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $createdLog = $true
    try {
        $sourceLogStream = [IO.File]::Open($buildLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { $sourceLogStream.CopyTo($outLogStream) } finally { $sourceLogStream.Dispose() }
    } finally { $outLogStream.Dispose() }
    if ((Get-FileHash -LiteralPath $buildLog -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $outLog -Algorithm SHA256).Hash) {
        throw 'The copied build log differs from the original; no installer was published.'
    }
    $tempOut = Join-Path $distDir ('.CloClo-Setup-' + $version + '-' + [Guid]::NewGuid().ToString('N') + '.partial.exe')
    $outStream = [IO.File]::Open($tempOut, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $setupStream = [IO.File]::Open($setup, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { $setupStream.CopyTo($outStream) } finally { $setupStream.Dispose() }
    } finally { $outStream.Dispose() }
    if ((Get-FileHash -LiteralPath $tempOut -Algorithm SHA256).Hash -ne $setupHash) {
        throw 'The copied setup differs from the verified build; no installer was published.'
    }
    $outSignature = Get-AuthenticodeSignature -LiteralPath $tempOut
    if ($CertThumbprint) {
        if ($outSignature.Status -notin 'Valid', 'UnknownError', 'NotTrusted' -or
            -not $outSignature.SignerCertificate -or
            $outSignature.SignerCertificate.Thumbprint -ine $CertThumbprint) {
            throw "The copied setup's signature does not match the requested certificate."
        }
    } elseif ($outSignature.Status -ne 'NotSigned') {
        throw "The copied setup was expected to be unsigned, but its signature status is $($outSignature.Status)."
    }
    [IO.File]::Move($tempOut, $out)
    $tempOut = $null
    $createdOut = $true
    exit 0
} catch {
    if ($tempOut) { Remove-Item -LiteralPath $tempOut -Force -ErrorAction SilentlyContinue }
    if ($createdLog) { Remove-Item -LiteralPath $outLog -Force -ErrorAction SilentlyContinue }
    if ($createdOut) { Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue }
    Write-Host $_.Exception.Message -ForegroundColor Red
    if ($buildLog -and (Test-Path -LiteralPath $buildLog)) {
        Write-Host "Failed build output was kept at $buildLog" -ForegroundColor Yellow
    }
    exit 1
} finally {
    if ($script:logWriter) { $script:logWriter.Dispose() }
}
