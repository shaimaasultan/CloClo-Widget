<#
.SYNOPSIS
  Adds (or with -Remove, removes) the Windows Firewall rules that hold the
  CloClo process to HTTPS on the public internet. Run as administrator.

.DESCRIPTION
  CloClo's own request layer (src\Platform\NetworkClient.cs) already refuses
  every host not on its list. These rules are the second fence, enforced by
  Windows for everything inside CloCloWidget.exe - its code, the Whisper and
  language-model runtimes, any library - even code that never goes through
  that layer:
    - outbound TCP only to port 443 (HTTPS); every other port blocked
    - outbound UDP blocked (no QUIC, no direct DNS; Windows' DNS Client
      service resolves names for CloClo)
    - ICMP (ping) blocked
    - nothing to the local network: LocalSubnet and the private, link-local
      and carrier-grade NAT ranges, IPv4 and IPv6
    - all inbound connections blocked
  They are block rules, which Windows applies over any allow rule, and they
  match only this exe's path, so other programs are untouched. Windows
  Firewall can't match host names, so which HTTPS hosts are allowed is the
  request layer's job; these rules make sure nothing in the process gets
  around it with another port, protocol or a device on your network.

  The MSI (CloClo-Setup.exe) and install.ps1 -ProgramFiles add the same rules
  themselves (Package.wxs, place.ps1); keep the three lists the same. This
  script is for a per-user install, whose exe lives in your profile:

    .\scripts\Set-CloCloFirewall.ps1 -ExePath "$env:LOCALAPPDATA\Programs\CloClo\CloCloWidget.exe"
    .\scripts\Set-CloCloFirewall.ps1 -ExePath ... -Remove

  netsh.exe is run from System32 by full path, never found through PATH.
#>
param(
    [Parameter(Mandatory)][string]$ExePath,
    [switch]$Remove
)
$env:PSModulePath = $PSHOME + '\Modules'
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an administrator PowerShell: Windows Firewall rules need administrator rights.'
}
$ExePath = [IO.Path]::GetFullPath($ExePath)
if ([IO.Path]::GetFileName($ExePath) -ne 'CloCloWidget.exe') { throw 'ExePath must point at CloCloWidget.exe.' }

$netsh = [IO.Path]::Combine([Environment]::GetFolderPath('System'), 'netsh.exe')
$program = "program=$ExePath"

# Every CloClo rule for this exe, old or new, is removed first.
& $netsh advfirewall firewall delete rule name=all $program | Out-Null
if ($Remove) { 'CloClo firewall rules removed.'; exit 0 }

$localNetworks = 'LocalSubnet,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,169.254.0.0/16,100.64.0.0/10,fc00::/7,fe80::/10'
$rules = @(
    @('name=CloClo: block outbound except HTTPS', 'dir=out', 'protocol=TCP', 'remoteport=1-442,444-65535'),
    @('name=CloClo: block outbound UDP', 'dir=out', 'protocol=UDP'),
    @('name=CloClo: block outbound ICMPv4', 'dir=out', 'protocol=icmpv4'),
    @('name=CloClo: block outbound ICMPv6', 'dir=out', 'protocol=icmpv6'),
    @('name=CloClo: block local network', 'dir=out', "remoteip=$localNetworks"),
    @('name=CloClo: block inbound', 'dir=in')
)
foreach ($rule in $rules) {
    & $netsh advfirewall firewall add rule @rule action=block $program enable=yes profile=any | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Windows Firewall refused the rule '$($rule[0])'." }
}
"CloClo firewall rules added for $ExePath."
