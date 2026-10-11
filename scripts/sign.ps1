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

  -Thumbprint selects a protected code-signing certificate in the
  CurrentUser or LocalMachine "My" store (including hardware tokens and
  cloud signing providers). File-based private keys are not accepted.
#>
param(
    [string]$Thumbprint,
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
# (Plain string concatenation, not Join-Path: Join-Path is itself a command
# that would be looked up - in the user's module folder - before this line
# takes effect. Reproduced with a look-alike Join-Path.)
$env:PSModulePath = $PSHOME + '\Modules'
$ErrorActionPreference = 'Stop'

if ($Thumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'Pass a 40-character certificate thumbprint.' }

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

# A certificate from the store may only sign if Windows guards its private
# key: either a key with "strong key protection", where Windows asks for
# confirmation every time anything signs with it, or a smart-card key that
# needs its PIN. Otherwise any program running as this user could sign its
# own files as CloClo without a trace.
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
    Assert-ProtectedKey $Thumbprint
    # SignTool accepts a batch, but Windows still authorizes every use of a
    # High-protection key. Keep that protection for each file.
    & $SignTool sign /sha1 $Thumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /q @files
    if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE)." }

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
