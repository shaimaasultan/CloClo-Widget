<#
.SYNOPSIS
  Records, or checks, the SHA-256 of every source file in the CloClo
  project folder - to see whether the code changed since the last install.

.DESCRIPTION
  -Create <file>  (used by install.ps1) writes a manifest of the project
                  folder: every file's SHA-256, the git commit, the time.
  (no -Create)    compares the project folder with the manifest next to this
                  script and lists files that were changed, added or removed.
                  Exit code 0 = identical, 1 = differences, 2 = can't check.

  install.ps1 copies this script and the manifest into the install folder.
  For a Program Files install only administrators can change either, so
  whatever changes the project folder can't also rewrite the record or the
  checker. Run it through Check-Source.cmd, which uses the installed copy.

  Build output (bin, obj, publish, dist, .vs, TestResults) and *.user files are
  left out: they change on every build and aren't source.
#>
param(
    [string]$Create,
    [string]$Root,
    # Plain concatenation: a parameter default is evaluated before the first
    # line below, so a command here would be looked up before the module
    # path is restricted.
    [string]$Manifest = ($PSScriptRoot + '\source-manifest.json'),
    [switch]$Quiet
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
$excludedDirs = 'bin', 'obj', 'publish', 'dist', '.vs', 'TestResults'

# Walks the folder itself instead of Get-ChildItem -Recurse, so links are
# never followed: a folder link (junction or symbolic link) could pull files
# from elsewhere into the record, hide where they really live, or loop. A
# link - to a folder or a file - is recorded as 'LINK:<target>' instead of
# being hashed, so adding, removing or retargeting one shows up as a change.
function Get-SourceHashes([string]$root) {
    $root = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\')
    $hashes = [ordered]@{}
    $entries = New-Object System.Collections.Generic.List[object]
    $pending = New-Object System.Collections.Generic.Stack[string]
    $pending.Push($root)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            $rel = $item.FullName.Substring($root.Length + 1)
            if ($rel.IndexOf('\') -lt 0 -and $item.PSIsContainer -and ($excludedDirs -contains $item.Name)) { continue }
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                $target = @($item.Target) -join ';'
                $entries.Add([pscustomobject]@{ Rel = $rel; Value = "LINK:$target" })
            } elseif ($item.PSIsContainer) {
                $pending.Push($item.FullName)
            } elseif ($item.Extension -ne '.user') {
                $entries.Add([pscustomobject]@{ Rel = $rel; Value = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash })
            }
        }
    }
    foreach ($e in ($entries | Sort-Object Rel)) { $hashes[$e.Rel] = $e.Value }
    $hashes
}

# Informational only: the commit the project folder is on, read straight from
# the .git folder's files (HEAD, then the branch's ref file or packed-refs).
# git itself is never run: it reads settings from .git\config and the user's
# .gitconfig - files any program running as this user can edit - and some
# of those settings (core.fsmonitor, for one) name a program for git to
# start, which would then run on every install and every check.
function Get-Commit([string]$root) {
    try {
        $dir = (Resolve-Path -LiteralPath $root).Path
        $gitDir = $null
        while ($dir) {
            $candidate = Join-Path $dir '.git'
            if (Test-Path -LiteralPath $candidate -PathType Container) { $gitDir = $candidate; break }
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                # A worktree or submodule: .git is a file naming the real folder.
                $line = (Get-Content -LiteralPath $candidate -TotalCount 1)
                if ($line -match '^gitdir:\s*(.+)$') { $gitDir = [IO.Path]::GetFullPath((Join-Path $dir $Matches[1].Trim())) }
                break
            }
            $dir = Split-Path $dir -Parent
        }
        if (-not $gitDir) { return 'unknown (not a git repository)' }
        $head = (Get-Content -LiteralPath (Join-Path $gitDir 'HEAD') -TotalCount 1).Trim()
        if ($head -match '^[0-9a-f]{40}$') { return $head }
        if ($head -notmatch '^ref:\s*(refs/\S+)$') { return 'unknown' }
        $ref = $Matches[1]
        $loose = Join-Path $gitDir ($ref -replace '/', '\')
        if (Test-Path -LiteralPath $loose) {
            $c = (Get-Content -LiteralPath $loose -TotalCount 1).Trim()
            if ($c -match '^[0-9a-f]{40}$') { return "$c ($($ref -replace '^refs/heads/', ''))" }
        }
        $packed = Join-Path $gitDir 'packed-refs'
        if (Test-Path -LiteralPath $packed) {
            foreach ($l in Get-Content -LiteralPath $packed) {
                if ($l -match '^([0-9a-f]{40}) (\S+)$' -and $Matches[2] -eq $ref) { return "$($Matches[1]) ($($ref -replace '^refs/heads/', ''))" }
            }
        }
    } catch { }
    'unknown'
}

# ---------------------------------------------------------------- create
if ($Create) {
    if (-not $Root) { throw '-Create needs -Root <project folder>.' }
    $root = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\')
    $record = [ordered]@{
        Root    = $root
        Created = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        Commit  = Get-Commit $root
        Files   = Get-SourceHashes $root
    }
    $record | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $Create -Encoding UTF8
    if (-not $Quiet) { Write-Host "Recorded $($record.Files.Count) source files from $root." }
    exit 0
}

# ---------------------------------------------------------------- compare
if (-not (Test-Path -LiteralPath $Manifest)) {
    Write-Host "No source record found at $Manifest - install CloClo with the current install.ps1 first." -ForegroundColor Yellow
    exit 2
}
$record = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
$root = if ($Root) { $Root } else { $record.Root }
if (-not (Test-Path -LiteralPath $root)) {
    Write-Host "The project folder $root doesn't exist." -ForegroundColor Yellow
    exit 2
}

$then = @{}
foreach ($p in $record.Files.PSObject.Properties) { $then[$p.Name] = $p.Value }
$now = Get-SourceHashes $root

$changed = @($now.Keys | Where-Object { $then.ContainsKey($_) -and $then[$_] -ne $now[$_] })
$added   = @($now.Keys | Where-Object { -not $then.ContainsKey($_) })
$removed = @($then.Keys | Where-Object { -not $now.Contains($_) } | Sort-Object)
$same    = $now.Count - $changed.Count - $added.Count

if ($Quiet) { exit $(if ($changed.Count + $added.Count + $removed.Count) { 1 } else { 0 }) }

Write-Host "Project folder: $root"
Write-Host "Compared with the install of $($record.Created) (commit $($record.Commit))."
Write-Host "Source record: $Manifest"
Write-Host ""
if ($changed.Count + $added.Count + $removed.Count -eq 0) {
    Write-Host "No changes: all $same source files match the installed build." -ForegroundColor Green
    exit 0
}
Write-Host "$same files unchanged." -ForegroundColor Green
if ($changed) { Write-Host "Changed ($($changed.Count)):" -ForegroundColor Yellow; $changed | ForEach-Object { "  $_" } }
if ($added)   { Write-Host "Added ($($added.Count)):" -ForegroundColor Yellow;     $added   | ForEach-Object { "  $_" } }
if ($removed) { Write-Host "Removed ($($removed.Count)):" -ForegroundColor Yellow; $removed | ForEach-Object { "  $_" } }
Write-Host ""
Write-Host "If you didn't make these changes yourself, look at them before building or installing again."
exit 1
