# place.ps1 - the only part of the CloClo install that runs as administrator.
#
# install.ps1 starts it through a short inline bootstrap - all that the UAC
# prompt's "Show more details" shows: this file's path and SHA-256, then the
# data. As administrator the bootstrap reads this file's bytes once, checks
# them against that SHA-256 and runs exactly those bytes from memory, so a
# copy swapped after the installer checked it never runs (exit 8). The data
# it sets first:
#   $Source            the folder with the built files (the project's publish\)
#   $Expected          'name=SHA-256;name=SHA-256;...' of every file as built
#   $AcceptThumbprint  a changed signing certificate (or 'unsigned') that the
#                      user confirmed with YES, otherwise ''
#
# Run as administrator, it trusts nothing it is handed:
# - the install folder is worked out here, from Windows' own record of
#   Program Files, so nothing passed in decides what is created or removed;
# - file names must be plain CloClo file names, and a source that is a link
#   is refused (a link could make this administrator copy read a file the
#   user can't);
# - everything is copied into a staging folder inside Program Files, which
#   only administrators can change, and checked there - so what was checked
#   is exactly what gets installed: the same set of files, the same SHA-256,
#   the exe signed by the same certificate as the installed copy (unless the
#   user accepted the change), each WPF DLL signed by Microsoft and each
#   Whisper CPU DLL signed by the new exe's signer;
# - then the folders are swapped by renaming, which fails as a whole while
#   the old copy is in use; on any failure the previous copy is put back.
# It writes no registry keys and no log files. The result is the exit code:
#   0 done, 2 the exe's signature is broken, 3 signed by another certificate
#   than the installed copy, 4 a DLL has an invalid or unexpected signer,
#   5 a source file is a link or has an unexpected name, 6 in use / other,
#   7 a copied file differs from the build or the file set doesn't match,
#   8 (from the bootstrap) this file changed after the installer checked it.

# (Plain string concatenation, not Join-Path: Join-Path is itself a command
# that would be looked up - in the user's module folder - before this line
# takes effect. Reproduced with a look-alike Join-Path.)
$env:PSModulePath = $PSHOME + '\Modules'
$ErrorActionPreference = 'Stop'
$exeName = 'CloCloWidget.exe'
$dest = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'CloClo'
$stage = $dest + '.new'
$old = $dest + '.old'
function Fail([int]$code) { throw ('exit ' + $code) }
function Clear-Dir([string]$path) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force } }
try {
Clear-Dir $stage
Clear-Dir $old
New-Item -ItemType Directory -Path $stage | Out-Null
$want = @{}
foreach ($pair in $Expected.Split(';')) {
$kv = $pair.Split('=', 2)
if ($kv.Count -ne 2 -or $kv[0] -notmatch '^[A-Za-z0-9_.-]+\.(exe|dll|ps1|json)$' -or $kv[1] -notmatch '^[0-9A-F]{64}$') { Fail 5 }
$want[$kv[0]] = $kv[1]
}
if (-not $want.ContainsKey($exeName)) { Fail 5 }
foreach ($n in @($want.Keys)) {
$item = Get-Item -LiteralPath (Join-Path $Source $n) -Force
if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { Fail 5 }
Copy-Item -LiteralPath $item.FullName -Destination $stage
}
# Checked inside Program Files: exactly the expected files, each exactly as built.
$staged = @(Get-ChildItem -LiteralPath $stage -Force)
if ($staged.Count -ne $want.Count) { Fail 7 }
foreach ($f in $staged) { if (-not $want.ContainsKey($f.Name) -or (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash -ne $want[$f.Name]) { Fail 7 } }
# The exe: an intact signature, from the same signer as the installed copy unless the user accepted the change.
$newSig = Get-AuthenticodeSignature -LiteralPath (Join-Path $stage $exeName)
if ($newSig.Status -in 'HashMismatch', 'NotSupportedFileFormat', 'Incompatible') { Fail 2 }
$newThumb = if ($newSig.SignerCertificate) { $newSig.SignerCertificate.Thumbprint } else { 'unsigned' }
$installed = Join-Path $dest $exeName
if (Test-Path -LiteralPath $installed) {
$oldSigner = (Get-AuthenticodeSignature -LiteralPath $installed).SignerCertificate
if ($oldSigner -and $oldSigner.Thumbprint -ne $newThumb -and $newThumb -ne $AcceptThumbprint) { Fail 3 }
}
# WPF DLLs are Microsoft-signed. The four optional local transcription DLLs
# are pinned during the build and signed by CloClo's own protected key.
$whisperDlls = @('ggml-base-whisper.dll', 'ggml-cpu-whisper.dll', 'ggml-whisper.dll', 'whisper.dll')
foreach ($f in @($staged | Where-Object { $_.Extension -eq '.dll' })) {
$s = Get-AuthenticodeSignature -LiteralPath $f.FullName
if ($f.Name -in $whisperDlls) {
    if ($newThumb -eq 'unsigned') {
        if ($s.Status -ne 'NotSigned') { Fail 4 }
    } elseif (-not $s.SignerCertificate -or $s.SignerCertificate.Thumbprint -ne $newThumb -or
            $s.Status -in 'HashMismatch', 'NotSupportedFileFormat', 'Incompatible', 'NotSigned') { Fail 4 }
} elseif ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation,') { Fail 4 }
}
# Swap the folders; renaming fails as a whole while the old copy is in use.
if (Test-Path -LiteralPath $dest) { Rename-Item -LiteralPath $dest -NewName (Split-Path $old -Leaf) }
Rename-Item -LiteralPath $stage -NewName (Split-Path $dest -Leaf)
Clear-Dir $old
# Earlier versions wrote the Installed apps entry for all users; it now lives in the user's own registry.
Remove-Item -LiteralPath 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CloClo' -Recurse -Force -ErrorAction SilentlyContinue
exit 0
} catch {
$code = if ($_.Exception.Message -match '^exit (\d)$') { [int]$Matches[1] } else { 6 }
try { Clear-Dir $stage } catch { }
if ((Test-Path -LiteralPath $old) -and -not (Test-Path -LiteralPath $dest)) { try { Rename-Item -LiteralPath $old -NewName (Split-Path $dest -Leaf) } catch { } }
exit $code
}
