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
  read from the SIGN_CERT_PASSWORD environment variable so it never shows
  up on a command line.
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

$ErrorActionPreference = 'Stop'

if (-not $Thumbprint -and -not $PfxFile) { throw 'Pass -Thumbprint or -PfxFile.' }
if ($PfxFile -and -not (Test-Path $PfxFile)) { throw "Certificate file not found: $PfxFile" }

if (-not $SignTool) {
    $SignTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $SignTool -or -not (Test-Path $SignTool)) {
    throw 'signtool.exe not found - install the Windows SDK, or pass -SignTool <path>.'
}

function Invoke-SignTool([string[]]$files) {
    $certArgs = if ($Thumbprint) { @('/sha1', $Thumbprint) }
                else {
                    $a = @('/f', $PfxFile)
                    if ($env:SIGN_CERT_PASSWORD) { $a += @('/p', $env:SIGN_CERT_PASSWORD) }
                    $a
                }
    # One signtool call for the whole batch: one timestamp round-trip per
    # file, but a single certificate lookup / PIN prompt.
    & $SignTool sign @certArgs /fd SHA256 /tr $TimestampUrl /td SHA256 /q @files
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

$toSign = @()
$signedRelative = @()
foreach ($line in Get-Content -LiteralPath $BundleList) {
    if (-not $line.Trim()) { continue }
    $source, $relative = $line.Split('|', 2)
    if ((Get-AuthenticodeSignature -LiteralPath $source).Status -eq 'Valid') { continue }

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
