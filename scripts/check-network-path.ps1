<#
.SYNOPSIS
  Run by the build (CheckSingleNetworkPath in CloCloWidget.csproj). Fails it
  when any C# file other than NetworkClient.cs creates its own HTTP client,
  socket or web request.

.DESCRIPTION
  Every internet request CloClo makes goes through NetworkClient.cs, which
  refuses any host not on its allow-list. This keeps a new feature from going
  online around that list: it has to add its host there instead.

  -ListFile  one full path per line: the C# files being compiled
#>
param(
    [Parameter(Mandatory)][string]$ListFile
)
$ErrorActionPreference = 'Stop'

# Matches "new HttpClient(", "new System.Net.Http.HttpClient(" and
# "HttpClient c = new(" alike.
$types = 'HttpClient|SocketsHttpHandler|HttpClientHandler|WebClient|TcpClient|UdpClient|ClientWebSocket|Socket'
$pattern = "new\s+([\w.]+\.)?($types)\s*\(|\b($types)\??\s+\w+\s*=\s*new\s*\(|WebRequest\.Create|HttpWebRequest|Windows\.Web\.Http"
$found = @()
foreach ($path in [IO.File]::ReadAllLines($ListFile)) {
    if (-not $path -or [IO.Path]::GetFileName($path) -eq 'NetworkClient.cs') { continue }
    if (-not [IO.File]::Exists($path)) { continue }
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        $lineNumber++
        if ($line -match $pattern) { $found += "${path}:${lineNumber}: $($line.Trim())" }
    }
}
if ($found.Count) {
    [Console]::Error.WriteLine('Network access outside NetworkClient.cs - every internet request must go through NetworkClient and its allow-list:')
    $found | ForEach-Object { [Console]::Error.WriteLine("  $_") }
    exit 1
}
