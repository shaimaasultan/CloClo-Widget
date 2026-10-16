<#
Builds a signed, self-contained CloClo MSIX with an individual Windows
microphone privacy switch. Run through Build-Setup.cmd. The old MSI cannot
upgrade to MSIX; uninstall it from Settings before installing this package.

The package must be signed by a publicly trusted certificate, so it installs
for the current user only, with no administrator rights and no certificate
import on any PC. This script never adds a certificate to any store, and it
refuses a signer that is self-signed or trusted only through a root that
was added for one user. Signers:

  Microsoft Artifact Signing (formerly Azure Trusted Signing), recommended:
    -SigningMetadata <metadata.json> -SigningDlib <Azure.CodeSigning.Dlib.dll>
    -Publisher "<certificate subject, exactly as the certificate profile shows it>"
    Sign in first with az login. Without arguments these three are read from
    %LOCALAPPDATA%\CloClo\signing\msix-signing.json:
      { "Metadata": "C:\\...\\metadata.json", "Dlib": "C:\\...\\Azure.CodeSigning.Dlib.dll",
        "Publisher": "CN=..., O=..., L=..., S=..., C=..." }

  A CA-issued certificate (for example on a hardware token):
    -CertThumbprint <sha1>

  -Unsigned builds a package for inspection only; Windows will not install it.
#>
param([string]$CertThumbprint, [string]$SigningMetadata, [string]$SigningDlib,
    [string]$Publisher, [switch]$Unsigned)

$env:PSModulePath = $PSHOME + '\Modules'
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, '..'))
$dotnet = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'dotnet\dotnet.exe'
$sdkBin = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10\bin'
$makeAppx = Get-ChildItem "$sdkBin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1 -ExpandProperty FullName
$stage = Join-Path $root 'obj\msix\stage'
$packageDir = Join-Path $root 'obj\msix\package'
$intermediateDir = Join-Path $root 'obj\msix\intermediate'
$dist = Join-Path $root 'dist'
$logWriter = $null
$tempPackage = $null
$tempLog = $null

function Say([string]$message) {
    Write-Host $message
    if ($script:logWriter) { $script:logWriter.WriteLine($message); $script:logWriter.Flush() }
}

function Clean-BuildDirectory([string]$path) {
    $full = [IO.Path]::GetFullPath($path).TrimEnd('\')
    $safe = [IO.Path]::GetFullPath((Join-Path $root 'obj\msix')).TrimEnd('\')
    if (-not $full.StartsWith($safe + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean outside obj\msix: $full"
    }
    foreach ($part in @($root, (Join-Path $root 'obj'), $safe, $full)) {
        if (Test-Path -LiteralPath $part) {
            $item = Get-Item -LiteralPath $part -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Unsafe build directory: $part"
            }
        }
    }
    if (Test-Path -LiteralPath $full) {
        $pending = New-Object System.Collections.Generic.Stack[string]
        $pending.Push($full)
        while ($pending.Count) {
            foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Refusing to follow a link in build output: $($item.FullName)"
                }
                if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            }
        }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
    New-Item -ItemType Directory -Path $full -Force | Out-Null
}

# The signer must chain to a root in the machine's own trust stores, which is
# what the Windows package installer checks. Roots a user added for
# themselves (CurrentUser\Root) do not count, and nothing is ever imported.
function Assert-PublicTrust([Security.Cryptography.X509Certificates.X509Certificate2]$cert) {
    if ($cert.Subject -eq $cert.Issuer) {
        throw "$($cert.Subject) is self-signed. Use Artifact Signing or a CA-issued certificate; CloClo never trusts a certificate for you."
    }
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
    # Expiry is judged by the timestamped signature (Valid); this checks the root.
    $chain.ChainPolicy.VerificationFlags = [Security.Cryptography.X509Certificates.X509VerificationFlags]::IgnoreNotTimeValid
    if (-not $chain.Build($cert)) {
        $why = ($chain.ChainStatus | ForEach-Object StatusInformation) -join ' '
        throw "$($cert.Subject) does not chain to a trusted root: $why"
    }
    $rootThumb = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate.Thumbprint
    if (-not (Test-Path -LiteralPath "Cert:\LocalMachine\Root\$rootThumb") -and
        -not (Test-Path -LiteralPath "Cert:\LocalMachine\AuthRoot\$rootThumb")) {
        throw "$($cert.Subject) is trusted only through a root added for one user. Windows will not install a package signed by it."
    }
}

function Invoke-Logged([string]$executable, [string[]]$arguments) {
    $old = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $executable @arguments 2>&1 | ForEach-Object { Say ([string]$_) }
        return $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
}

try {
    if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator) -and
        (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue).EnableLUA -ne 0) {
        throw 'Build from a normal, non-administrator window.'
    }
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw ".NET SDK not found at $dotnet" }
    if (-not $makeAppx) { throw 'makeappx.exe not found. Install the Windows SDK.' }
    if ($CertThumbprint -and $CertThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'CertThumbprint must be 40 hex characters.' }
    $signArgs = @()
    $publishSignArgs = @('-p:SignCertThumbprint=')
    if ($Unsigned) {
        $CertThumbprint = ''
        $Publisher = 'CN=CloClo'
    } elseif ($CertThumbprint) {
        if ($SigningMetadata -or $SigningDlib) { throw 'Pass either -CertThumbprint or Artifact Signing, not both.' }
        $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $CertThumbprint -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
            Select-Object -First 1
        if (-not $cert) { throw "No usable code-signing certificate $CertThumbprint in your certificate store." }
        Assert-PublicTrust $cert
        if ($Publisher -and $Publisher -cne $cert.Subject) { throw "-Publisher does not match the certificate subject $($cert.Subject)." }
        $Publisher = $cert.Subject
        $signArgs = @('-Thumbprint', $CertThumbprint)
        $publishSignArgs = @("-p:SignCertThumbprint=$CertThumbprint")
    } else {
        if (-not ($SigningMetadata -or $SigningDlib -or $Publisher)) {
            $config = [IO.Path]::Combine([Environment]::GetFolderPath('LocalApplicationData'), 'CloClo', 'signing', 'msix-signing.json')
            if (-not (Test-Path -LiteralPath $config -PathType Leaf)) {
                throw "No signer given and $config does not exist. See the top of scripts\build-msix.ps1."
            }
            $settings = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
            $SigningMetadata = [string]$settings.Metadata
            $SigningDlib = [string]$settings.Dlib
            $Publisher = [string]$settings.Publisher
        }
        if (-not ($SigningMetadata -and $SigningDlib -and $Publisher)) {
            throw 'Artifact Signing needs -SigningMetadata, -SigningDlib and -Publisher.'
        }
        $SigningMetadata = [IO.Path]::GetFullPath($SigningMetadata)
        $SigningDlib = [IO.Path]::GetFullPath($SigningDlib)
        $signArgs = @('-SigningMetadata', $SigningMetadata, '-SigningDlib', $SigningDlib)
        $publishSignArgs = @("-p:SignMetadataFile=$SigningMetadata", "-p:SignDlibPath=$SigningDlib")
    }
    if (-not $Unsigned) {
        $signArgs += '-RequireTrusted'
        $publishSignArgs += '-p:SignRequireTrusted=true'
    }
    # An MSIX identity publisher must match the signing certificate subject
    # exactly. The unsigned-package OID would only ever install machine-wide.
    if ($Publisher -notmatch '^CN=' -or $Publisher -match '2\.25\.311729368913984317654407730594956997722') {
        throw "Publisher must be the certificate subject, starting with CN=: $Publisher"
    }
    $publisher = [Security.SecurityElement]::Escape($Publisher)

    if (Test-Path -LiteralPath $dist) {
        $item = Get-Item -LiteralPath $dist -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Unsafe dist directory: $dist"
        }
    } else { New-Item -ItemType Directory -Path $dist | Out-Null }

    $now = Get-Date
    $minor = $now.Year - 2000
    $build = $now.DayOfYear * 150 + [int][Math]::Floor(($now.Hour * 60 + $now.Minute) / 10)
    do {
        if ($build -gt 65535) { throw 'No MSIX version remains for this year.' }
        $version = "1.$minor.$build.0"
        $out = Join-Path $dist "CloClo-$version.msix"
        $outLog = Join-Path $dist "CloClo-$version.txt"
        $build++
    } while ((Test-Path -LiteralPath $out) -or (Test-Path -LiteralPath $outLog))

    Clean-BuildDirectory $stage
    Clean-BuildDirectory $packageDir
    Clean-BuildDirectory $intermediateDir
    $buildLog = Join-Path $packageDir 'build.txt'
    $script:logWriter = [IO.StreamWriter]::new($buildLog, $false, [Text.UTF8Encoding]::new($false))
    Say "CloClo MSIX $version"
    Say "Publisher: $publisher"
    if ($Unsigned) { Say 'WARNING: unsigned package for local inspection only; Windows cannot install it.' }

    Say '1/3 Publishing the signed CloClo application...'
    $publish = @('publish', (Join-Path $root 'CloCloWidget.csproj'), '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '--disable-build-servers', '-nodeReuse:false', '-nologo', '-v:minimal',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=false', '-p:DebugType=none',
        '-o', $stage, "-p:Version=$version") + $publishSignArgs
    if ((Invoke-Logged $dotnet $publish) -ne 0) { throw 'CloClo publish failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $stage 'CloCloWidget.exe') -PathType Leaf)) {
        throw 'Publish did not produce CloCloWidget.exe.'
    }

    # Accept only the app's exact published layout. A stray file in the stage
    # cannot silently enter a signed package.
    $allowedRoot = @('CloCloWidget.exe', 'ggml-base.bin', 'ggml-large-v3-turbo-q5_0.bin',
        'D3DCompiler_47_cor3.dll', 'PenImc_cor3.dll', 'PresentationNative_cor3.dll',
        'vcruntime140_cor3.dll', 'wpfgfx_cor3.dll', 'ggml-base-whisper.dll',
        'ggml-cpu-whisper.dll', 'ggml-whisper.dll', 'whisper.dll')
    $allowedGpu = @('ggml-base-whisper.dll', 'ggml-cpu-whisper.dll',
        'ggml-vulkan-whisper.dll', 'ggml-whisper.dll', 'whisper.dll')
    foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse) {
        $relative = $file.FullName.Substring($stage.Length + 1)
        $rootFile = $relative -notmatch '\\'
        if (($rootFile -and $relative -notin $allowedRoot) -or
            (-not $rootFile -and ($relative -notmatch '^gpu\\[^\\]+$' -or $file.Name -notin $allowedGpu))) {
            throw "Unexpected published file: $relative"
        }
    }
    foreach ($name in $allowedRoot) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $name) -PathType Leaf)) { throw "Missing package file: $name" }
    }
    foreach ($name in $allowedGpu) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage "gpu\$name") -PathType Leaf)) {
            throw "Missing GPU package file: $name"
        }
    }

    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push($stage)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Refusing to package a linked file or directory: $($item.FullName)"
            }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }

    $assets = Join-Path $stage 'Assets'
    New-Item -ItemType Directory -Path $assets | Out-Null
    foreach ($name in @('StoreLogo.png', 'Square44x44Logo.png', 'Square150x150Logo.png')) {
        Copy-Item -LiteralPath (Join-Path $root "installer\msix\$name") -Destination (Join-Path $assets $name)
    }
    $template = [IO.File]::ReadAllText((Join-Path $root 'installer\msix\AppxManifest.xml.in'))
    $manifest = $template.Replace('__VERSION__', $version).Replace('__PUBLISHER__', $publisher)
    [IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

    Say '2/3 Packing the MSIX...'
    $package = Join-Path $packageDir 'CloClo.msix'
    if ((Invoke-Logged $makeAppx @('pack', '/d', $stage, '/p', $package, '/o')) -ne 0) {
        throw 'MakeAppx could not build the MSIX.'
    }
    if (-not $Unsigned) {
        Say '3/3 Signing the MSIX...'
        $powershell = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
        if ((Invoke-Logged $powershell (@('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
            '-File', (Join-Path $root 'scripts\sign.ps1'), '-Path', $package) + $signArgs)) -ne 0) {
            throw 'MSIX signing failed.'
        }
        $signature = Get-AuthenticodeSignature -LiteralPath $package
        if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or
            $signature.SignerCertificate.Subject -cne $Publisher -or
            ($CertThumbprint -and $signature.SignerCertificate.Thumbprint -ine $CertThumbprint)) {
            throw "MSIX signature verification failed: $($signature.Status) $($signature.SignerCertificate.Subject)"
        }
        Assert-PublicTrust $signature.SignerCertificate
        Say "Signer: $($signature.SignerCertificate.Subject) ($($signature.SignerCertificate.Thumbprint))"
    } else { Say '3/3 Signing skipped (unsigned package).' }

    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
    Say "Package: $out"
    Say "SHA-256: $hash"
    Say "Build log: $outLog"
    Say 'Uninstall the old MSI CloClo before installing this MSIX. Double-click the .msix to install it for the current user only; no certificate import or administrator rights are needed.'
    $script:logWriter.Dispose(); $script:logWriter = $null

    $tempLog = Join-Path $dist ('.CloClo-' + [Guid]::NewGuid().ToString('N') + '.partial.txt')
    $tempPackage = Join-Path $dist ('.CloClo-' + [Guid]::NewGuid().ToString('N') + '.partial.msix')
    Copy-Item -LiteralPath $buildLog -Destination $tempLog
    Copy-Item -LiteralPath $package -Destination $tempPackage
    if ((Get-FileHash -LiteralPath $tempPackage -Algorithm SHA256).Hash -ne $hash) { throw 'Copied package hash differs.' }
    if ((Get-FileHash -LiteralPath $tempLog -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $buildLog -Algorithm SHA256).Hash) { throw 'Copied build log differs.' }
    [IO.File]::Move($tempLog, $outLog); $tempLog = $null
    [IO.File]::Move($tempPackage, $out); $tempPackage = $null
    exit 0
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
} finally {
    if ($script:logWriter) { $script:logWriter.Dispose() }
    if ($tempPackage -and (Test-Path -LiteralPath $tempPackage)) { Remove-Item -LiteralPath $tempPackage -Force }
    if ($tempLog -and (Test-Path -LiteralPath $tempLog)) { Remove-Item -LiteralPath $tempLog -Force }
}
