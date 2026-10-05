<#
.SYNOPSIS
  Authenticode-signs CloClo's binaries. Called by the build (see the
  SignBundledBinaries / SignPublishedExe targets in CloCloWidget.csproj),
  which install.ps1 runs - not usually invoked by hand.

.DESCRIPTION
  Two modes:

  -BundleList <file> -OutDir <dir> -SignedList <file>
      Runs just before the single-file bundle is built. BundleList holds
      "full path|relative path" lines for every DLL about to go into the
      bundle. Each one that isn't already validly signed (the app's own
      CloCloWidget.dll, and any unsigned third-party library) is copied to
      OutDir\<relative path> and the copy is signed - originals, which can
      live in the shared NuGet cache, are never modified. The relative
      (in-bundle) paths of the files that were signed are written to
      SignedList so the build can bundle the signed copies instead.

  -Path <file>
      Signs one file in place (the final published exe).

  The certificate is either -Thumbprint (a code-signing cert in the
  CurrentUser or LocalMachine "My" store - also where hardware tokens and
  cloud signing providers expose theirs) or -PfxFile, whose password is
  read from the SIGN_CERT_PASSWORD environment variable. A .pfx is never
  handed to signtool directly - that needs the password on signtool's
  command line, readable by any other process. Instead it's loaded here,
  placed in the CurrentUser "My" store just for the signing, used by
  thumbprint, then removed again together with its private key.
#>
param(
    [string]$Thumbprint,
    [string]$PfxFile,
    [string]$BundleList,
    [string]$OutDir,
    [string]$SignedList,
    [string]$Path,
    [string]$SignTool,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

# Only Windows PowerShell's own modules, from its system folder - set before
# any command is used, so a look-alike module planted in the user's
# Documents\WindowsPowerShell\Modules can't stand in for Get-FileHash and the
# other commands this relies on.
$env:PSModulePath = Join-Path $PSHOME 'Modules'
$ErrorActionPreference = 'Stop'

if (-not $Thumbprint -and -not $PfxFile) { throw 'Pass -Thumbprint or -PfxFile.' }
if ($PfxFile -and -not (Test-Path $PfxFile)) { throw "Certificate file not found: $PfxFile" }

if (-not $SignTool) {
    # Known folder, not ${env:ProgramFiles(x86)}, which this user's
    # environment could redirect to a look-alike signtool.
    $kits = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10\bin'
    $SignTool = Get-ChildItem "$kits\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $SignTool -or -not (Test-Path $SignTool)) {
    throw 'signtool.exe not found - install the Windows SDK, or pass -SignTool <path>.'
}

# Makes the .pfx's certificate available to signtool by thumbprint for the
# duration of $action only. A certificate that was already in the store
# beforehand is left exactly as it was.
function Use-PfxCertificate([scriptblock]$action) {
    $flags = [Security.Cryptography.X509Certificates.X509KeyStorageFlags]'UserKeySet, PersistKeySet'
    $cert = [Security.Cryptography.X509Certificates.X509Certificate2]::new($PfxFile, $env:SIGN_CERT_PASSWORD, $flags)
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('My', 'CurrentUser')
    $store.Open('ReadWrite')
    $alreadyThere = $store.Certificates.Find('FindByThumbprint', $cert.Thumbprint, $false).Count -gt 0
    try {
        if (-not $alreadyThere) { $store.Add($cert) }
        & $action $cert.Thumbprint
    } finally {
        if (-not $alreadyThere) {
            $store.Remove($cert)
            # Removing the certificate leaves its private key behind in the
            # user's key store - delete that too.
            $key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
            if ($key -is [Security.Cryptography.RSACng]) { $key.Key.Delete() }
            elseif ($key -is [Security.Cryptography.RSACryptoServiceProvider]) { $key.PersistKeyInCsp = $false; $key.Clear() }
        }
        $store.Close()
    }
}

# A certificate from the store may only sign if Windows guards its private
# key: either a key with "strong key protection", where Windows asks for
# confirmation every time anything signs with it, or a smart-card key that
# needs its PIN. Otherwise any program running as this user could sign its
# own files as CloClo without a trace. (A .pfx is protected by its
# password instead, so that path isn't affected.)
function Assert-ProtectedKey([string]$thumbprint) {
    $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object Thumbprint -eq $thumbprint | Select-Object -First 1
    if (-not $cert) { throw "No certificate with thumbprint $thumbprint in your certificate store." }
    if (-not $cert.HasPrivateKey) { throw "The certificate $thumbprint has no private key here, so it can't sign." }

    $key = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    if (-not $key) { $key = [Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($cert) }
    $protected = $false
    $how = 'an unprotected software key'
    if ($key -is [Security.Cryptography.RSACng] -or $key -is [Security.Cryptography.ECDsaCng]) {
        $cng = $key.Key
        $level = $cng.UIPolicy.ProtectionLevel
        if ($level -ne [Security.Cryptography.CngUIProtectionLevels]::None) { $protected = $true; $how = "a protected key ($level)" }
        elseif ($cng.Provider.Provider -eq 'Microsoft Smart Card Key Storage Provider') { $protected = $true; $how = 'a smart-card key' }
    } elseif ($key -is [Security.Cryptography.RSACryptoServiceProvider]) {
        $info = $key.CspKeyContainerInfo
        if ($info.Protected -or $info.HardwareDevice) { $protected = $true; $how = 'a protected or hardware key' }
    }
    if (-not $protected) {
        throw ("The certificate $($cert.Subject) has $how - any program running as you could sign with it silently. " +
            "Create a protected one instead (Windows will then ask you to confirm each signing):`n" +
            "  New-SelfSignedCertificate -Type CodeSigningCert -Subject `"CN=CloClo`" -CertStoreLocation Cert:\CurrentUser\My -KeyProtection Protect -NotAfter (Get-Date).AddYears(5)`n" +
            "See README > Signing with your own certificate.")
    }
    Write-Host "Signing key: $how [$($cert.Subject)]"
}

function Invoke-SignTool([string[]]$files) {
    $sign = {
        param($thumbprint)
        # One signtool call for the whole batch: one timestamp round-trip
        # per file, but a single certificate lookup / PIN prompt.
        & $SignTool sign /sha1 $thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /q @files
        if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE)." }
    }
    if ($Thumbprint) { Assert-ProtectedKey $Thumbprint; & $sign $Thumbprint } else { Use-PfxCertificate $sign }

    foreach ($f in $files) {
        $sig = Get-AuthenticodeSignature -LiteralPath $f
        switch ($sig.Status) {
            'Valid' { }
            # Signed and intact, but the certificate doesn't chain to a root
            # this machine trusts - expected with a self-signed test cert.
            { $_ -in 'UnknownError', 'NotTrusted' } {
                Write-Warning "$([IO.Path]::GetFileName($f)): signed, but the certificate isn't trusted on this machine ($($sig.StatusMessage))."
            }
            default { throw "$f failed verification after signing: $($sig.Status) - $($sig.StatusMessage)" }
        }
    }
}

if ($Path) {
    Invoke-SignTool @((Resolve-Path -LiteralPath $Path).Path)
    Write-Host "Signed $Path"
    return
}

if (-not ($BundleList -and $OutDir -and $SignedList)) { throw 'Bundle mode needs -BundleList, -OutDir and -SignedList.' }

# First check every file, then copy: a tampered file stops the build before
# anything is copied or signed.
$unsigned = @()
foreach ($line in Get-Content -LiteralPath $BundleList) {
    if (-not $line.Trim()) { continue }
    $source, $relative = $line.Split('|', 2)
    # Only a file with no signature at all gets ours. A file that carries a
    # signature which no longer verifies has been changed since its
    # publisher signed it - most likely a runtime DLL tampered with in the
    # NuGet cache, which any program running as this user can write to.
    # Signing over it would launder it under CloClo's name, so stop instead.
    $status = (Get-AuthenticodeSignature -LiteralPath $source).Status
    if ($status -eq 'Valid') { continue }
    if ($status -ne 'NotSigned') {
        throw "$source carries a signature that doesn't verify ($status) - it was changed after its publisher signed it. Not signing over it. Clear that package from the NuGet cache (dotnet nuget locals all --clear) and build again."
    }
    $unsigned += , @($source, $relative)
}

$toSign = @()
$signedRelative = @()
foreach ($entry in $unsigned) {
    $source, $relative = $entry
    $copy = Join-Path $OutDir $relative
    New-Item -ItemType Directory -Force -Path (Split-Path $copy) | Out-Null
    Copy-Item -LiteralPath $source -Destination $copy -Force
    $toSign += $copy
    $signedRelative += $relative
}

if ($toSign.Count -gt 0) {
    Invoke-SignTool $toSign
    $toSign | ForEach-Object { Write-Host "Signed $(Split-Path $_ -Leaf)" }
}
Set-Content -LiteralPath $SignedList -Value $signedRelative
