<#
.SYNOPSIS
  Run by the build (GenerateNativeLibraryPins in CloCloWidget.csproj) before
  it records the SHA-256 of the native runtime DLLs it ships. Makes sure
  every one of them is exactly the file inside its signed NuGet package.

.DESCRIPTION
  The DLLs come from the unpacked NuGet cache (%UserProfile%\.nuget\packages),
  which any program running as this user can write to - and whatever is there
  at build time gets pinned as "genuine" and compiled into the signed exe.
  NuGet only checks a package's signature when it first unpacks it. So, for
  each package the DLLs come from, this:
    1. runs "dotnet nuget verify --all" on the package file (.nupkg) kept in
       the cache: every signature must be intact and chain to a trusted root,
       and the author signature must be Microsoft Corporation's;
    2. checks that the package's own id and version (inside it) match the
       cache folder it sits in, so an older genuine package can't stand in;
    3. compares every listed DLL byte-for-byte (SHA-256) with its entry inside
       that verified package.
  Any failure stops the build. The fix is to clear the cache
  (dotnet nuget locals global-packages --clear) and build again.

  -ListFile  one full path per line: the DLLs about to be pinned
  -Dotnet    full path of dotnet.exe (never looked up through PATH)
#>
param(
    [Parameter(Mandatory)][string]$ListFile,
    [Parameter(Mandatory)][string]$Dotnet
)
# Only Windows PowerShell's own modules, from its system folder - set before
# any command is used, so a look-alike module planted in the user's
# Documents\WindowsPowerShell\Modules can't stand in for Get-FileHash.
# (Plain string concatenation, not Join-Path: Join-Path is itself a command
# that would be looked up - in the user's module folder - before this line
# takes effect. Reproduced with a look-alike Join-Path.)
$env:PSModulePath = $PSHOME + '\Modules'
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$sha = [Security.Cryptography.SHA256]::Create()
function Hex([byte[]]$bytes) { [BitConverter]::ToString($bytes) -replace '-', '' }

$files = @(Get-Content -LiteralPath $ListFile | Where-Object { $_.Trim() })
$byPackage = $files | Group-Object {
    $i = $_.IndexOf('\runtimes\', [StringComparison]::OrdinalIgnoreCase)
    if ($i -lt 0) { throw "$_ isn't inside a NuGet package's runtimes folder." }
    $_.Substring(0, $i)
}

foreach ($group in $byPackage) {
    $pkgRoot = $group.Name
    $version = Split-Path $pkgRoot -Leaf
    $id = Split-Path (Split-Path $pkgRoot -Parent) -Leaf
    $nupkg = Join-Path $pkgRoot "$id.$version.nupkg"
    if (-not (Test-Path -LiteralPath $nupkg)) { throw "The package file $nupkg is missing from the NuGet cache, so $id can't be verified." }

    # 1. Signatures: intact, trusted, author = Microsoft Corporation.
    $out = & $Dotnet nuget verify --all $nupkg -v detailed 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "The signature of $id $version doesn't verify:`n$out" }
    # The first "Subject Name" after "Signature type: Author" is the signer.
    $author = [regex]::Match($out, 'Signature type: Author[\s\S]*?Subject Name: ([^\r\n]+)')
    if (-not $author.Success -or $author.Groups[1].Value -notmatch '(^|, )O=Microsoft Corporation(,|$)') {
        throw "$id $version isn't author-signed by Microsoft Corporation."
    }
    # And it came from nuget.org: its repository signature names that feed.
    if ($out -notmatch 'Signature type: Repository[\s\S]*?Service index: https://api\.nuget\.org/v3/index\.json') {
        throw "$id $version doesn't carry nuget.org's repository signature."
    }

    $zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        # 2. The package says it's this id and version.
        $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -notmatch '/' -and $_.Name -like '*.nuspec' } | Select-Object -First 1
        $reader = New-Object IO.StreamReader($nuspecEntry.Open())
        try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($nuspec.package.metadata.id -ne $id -or $nuspec.package.metadata.version -ne $version) {
            throw "The package in $pkgRoot says it's $($nuspec.package.metadata.id) $($nuspec.package.metadata.version), not $id $version."
        }

        # 3. Each DLL is byte-for-byte its entry inside the package.
        foreach ($file in $group.Group) {
            $entryName = $file.Substring($pkgRoot.Length + 1).Replace('\', '/')
            $entry = $zip.GetEntry($entryName)
            if (-not $entry) { throw "$entryName isn't in $id $version." }
            $stream = $entry.Open()
            try { $inPackage = Hex $sha.ComputeHash($stream) } finally { $stream.Dispose() }
            $onDisk = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
            if ($inPackage -ne $onDisk) {
                throw "$file doesn't match the file in its signed package ($id $version) - the NuGet cache was changed. Clear it (dotnet nuget locals global-packages --clear) and build again."
            }
        }
    } finally {
        $zip.Dispose()
    }
    Write-Host "Verified $($group.Count) native DLLs against the signed package $id $version."
}
