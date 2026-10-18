<#
.SYNOPSIS
  Packs Quran recitations into signed, code-free DLLs. Called by install.ps1;
  not usually run by hand.
.DESCRIPTION
  Each folder Assets\Recitations\<reciter>\ holding per-ayah files named
  SSSAAA.mp3 (everyayah.com's naming) becomes one
  <OutDir>\Recitations\CloClo.Recitations.<reciter>.dll: a .NET assembly with
  no code at all, only the MP3s as embedded resources named "SSSAAA.mp3".
  With -Thumbprint it is Authenticode-signed by sign.ps1 with the same
  protected certificate as CloClo itself, and CloClo reads a pack only when
  Windows confirms the signature is intact and its signer is the exe's own
  (see RecitationPack.cs). It is never loaded as code: CloClo reads the
  resources straight out of the file.

  Only plain names are taken (reciter folders [A-Za-z0-9_.-]{1,64}, files
  SSSAAA.mp3 up to 30 MB); anything else is left out, and a link anywhere
  stops the install. The packs are built under obj\recitations\, which
  Check-Source leaves out like any build output.
#>
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$DotnetExe,
    [string]$Thumbprint
)

# Only Windows PowerShell's own modules (see the note in install.ps1).
$env:PSModulePath = $PSHOME + '\Modules'
$ErrorActionPreference = 'Stop'

$source = $Root + '\Assets\Recitations'
if (-not (Test-Path -LiteralPath $source -PathType Container)) { return }
function Assert-NotLink($item) {
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Assets\Recitations contains a folder or file link ('$($item.Name)'); remove it and run the install again."
    }
}
Assert-NotLink (Get-Item -LiteralPath $source -Force)

$packs = @()
foreach ($reciter in @(Get-ChildItem -LiteralPath $source -Force -Directory | Sort-Object Name)) {
    Assert-NotLink $reciter
    if ($reciter.Name -cnotmatch '^[A-Za-z0-9_.-]{1,64}$') {
        Write-Warning "Skipping Assets\Recitations\$($reciter.Name): use only letters, digits, '_', '.' and '-' in a reciter folder name."
        continue
    }
    $mp3s = @()
    foreach ($f in @(Get-ChildItem -LiteralPath $reciter.FullName -Force -File | Sort-Object Name)) {
        Assert-NotLink $f
        if ($f.Name -cmatch '^[0-9]{6}\.mp3$' -and $f.Length -gt 0 -and $f.Length -le 30MB) { $mp3s += $f }
    }
    if (-not $mp3s.Count) { continue }

    $name = 'CloClo.Recitations.' + $reciter.Name
    $work = $Root + '\obj\recitations\' + $reciter.Name
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    New-Item -ItemType Directory -Path $work | Out-Null
    $resources = ($mp3s | ForEach-Object {
        '    <EmbeddedResource Include="' + [Security.SecurityElement]::Escape($_.FullName) + '" LogicalName="' + $_.Name + '" />'
    }) -join "`r`n"
    # No source files at all: the assembly holds only the recitations.
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>$name</AssemblyName>
    <EnableDefaultItems>false</EnableDefaultItems>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <GenerateDependencyFile>false</GenerateDependencyFile>
    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
    <ImplicitUsings>disable</ImplicitUsings>
    <DebugType>none</DebugType>
    <Deterministic>true</Deterministic>
    <NoWarn>CS2008</NoWarn>
  </PropertyGroup>
  <ItemGroup>
$resources
  </ItemGroup>
</Project>
"@
    Set-Content -LiteralPath ($work + '\' + $name + '.csproj') -Value $project -Encoding UTF8
    & $DotnetExe build ($work + '\' + $name + '.csproj') -c Release -o ($work + '\out') -nologo -v quiet `
        --disable-build-servers -nodeReuse:false | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Packing the $($reciter.Name) recitations failed (dotnet build exited with $LASTEXITCODE)." }

    $built = $work + '\out\' + $name + '.dll'
    $target = $OutDir + '\Recitations'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -LiteralPath $built -Destination $target
    $packed = $target + '\' + $name + '.dll'
    if ($Thumbprint) {
        & ($Root + '\scripts\sign.ps1') -Path $packed -Thumbprint $Thumbprint
    }
    $packs += [pscustomobject]@{ Reciter = $reciter.Name; Files = $mp3s.Count; Path = $packed }
    Write-Host ("Packed {0} recitations of {1} into Recitations\{2}.dll ({3:N0} MB){4}." -f $mp3s.Count, $reciter.Name, $name,
        ((Get-Item -LiteralPath $packed).Length / 1MB), $(if ($Thumbprint) { ', signed' } else { ', unsigned' }))
}
$packs
