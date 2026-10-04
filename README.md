# CloClo Desktop Widget

A small always-on-top floating widget for Windows: the Keeper avatar from the
CloClo app, dressed for the real current weather at a location you set,
sitting near your taskbar clock (Windows doesn't allow any app to draw
directly on the system clock itself, so this is the closest a normal app can
get — the bottom-right corner, just above the taskbar).

## Running it

The ready-to-run build is in `publish\CloCloWidget.exe` — just double-click
it. No install, no .NET required (it's self-contained). Windows may show a
SmartScreen prompt the first time since the .exe isn't code-signed; choose
"More info" → "Run anyway".

## Using it

- **Drag** anywhere on the widget to move it. It remembers where you leave it.
- **Right-click** for a menu: set your location, refresh the weather, toggle
  always-on-top, or exit.
- A tray icon (near the clock, in the hidden-icons area) does the same —
  double-click it to show/hide the widget if you ever lose track of it.
- **Privacy** (right-click menu): the widget is hidden from screenshots,
  recordings and screen sharing by default, and notification text can be
  turned off so only the sending app's name shows.
- Dictated text is saved under `%LocalAppData%\CloCloWidget\Dictation`
  (right-click → "Open dictation folder").
- Weather refreshes automatically every 20 minutes, from
  [Open-Meteo](https://open-meteo.com) (no API key, no account).

## Installing

One command builds, signs (optional), installs for the current user (no admin
needed) and adds a Start Menu shortcut — run it again any time to upgrade:

```powershell
.\install.ps1                                   # unsigned
.\install.ps1 -CertThumbprint <thumbprint>      # signed, cert from your certificate store
.\install.ps1 -PfxPath C:\certs\codesign.pfx    # signed, cert from a .pfx (asks for its password)
.\install.ps1 -DesktopShortcut                  # also put a shortcut on the desktop
.\install.ps1 -Uninstall
```

If Windows says running scripts is disabled, start it as
`powershell -ExecutionPolicy Bypass -File .\install.ps1` (plus any options) —
that allows this one run without changing any system setting.

It installs to `%LocalAppData%\Programs\CloCloWidget`, closes a running
widget first so the exe can be replaced, and moves "Start with Windows" to the
installed copy if it was on.

### Signing

With a certificate, `sign.ps1` runs inside the build in two stages, since the
single-file exe carries every DLL inside it:

1. Just before bundling, every DLL that isn't already validly signed — the
   app's own `CloCloWidget.dll` and unsigned third-party ones such as
   `QRCoder.dll` — is copied to `obj\...\signed\`, the copy is signed, and the
   bundle is built from the signed copies. Microsoft's runtime DLLs keep their
   own signatures, and nothing in the NuGet cache is modified.
2. The finished `CloCloWidget.exe` is signed.

Everything is SHA-256 signed with an RFC 3161 timestamp (so signatures stay
valid after the certificate expires) and checked right after signing; any
failure fails the build. A `.pfx` password is passed through the
`SIGN_CERT_PASSWORD` environment variable, never on a command line: the
certificate is loaded in memory, placed in your personal certificate store
only while signing, and removed again afterwards along with its private key. Any
Authenticode code-signing certificate works; Azure Trusted Signing is the
cheapest option for an individual developer.

#### Signing with your own certificate (left untrusted)

A self-signed certificate signs everything exactly like a bought one; the only
difference is that no Windows PC trusts it, so the publisher still shows as
unverified. It stays that way on purpose — it's never added to Windows'
trusted roots. Run everything below in PowerShell from the `cloclo-widget`
folder.

**First time — create the certificate** (once; it's valid for five years):

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=CloClo" -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
```

**Every time — sign and install.** In a new PowerShell window, first load the
certificate (this picks the newest CloClo certificate, so it works even if
there's more than one); in the window you just created it in, `$cert` is
already set and you can skip this line:

```powershell
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq "CN=CloClo" | Sort-Object NotBefore -Descending | Select-Object -First 1
```

Then build, sign and install:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -CertThumbprint $cert.Thumbprint -DesktopShortcut
```

`install.ps1` closes a running widget by itself. Only a copy that was started
as administrator can't be closed that way; the script then says so — close it
with right-click → Exit and run the command again.

The build prints "signed, but the certificate isn't trusted on this machine"
for each file, and the last line reads "signed, but this PC doesn't trust the
certificate yet". Both are expected.

**Replacing the certificate** — delete every CloClo certificate together with
its private key (other certificates aren't touched), then create a new one
and sign and install as above:

```powershell
Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq "CN=CloClo" | ForEach-Object { Remove-Item -Path "Cert:\CurrentUser\My\$($_.Thumbprint)" -DeleteKey }
```

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=CloClo" -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
```

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -CertThumbprint $cert.Thumbprint -DesktopShortcut
```

Deleting the key is permanent, and already-signed copies keep their
signature. Keep the private key to yourself — never export the certificate
with its key.

## Rebuilding from source

```bash
dotnet build
dotnet run
```

To produce another standalone .exe:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

To sign it the same way `install.ps1` does, add
`-p:SignCertThumbprint=<thumbprint>`, or `-p:SignCertFile=<path.pfx>` with the
password in `SIGN_CERT_PASSWORD`.

## How it's built

A WPF host window (transparent, borderless, topmost, no taskbar entry,
click-through) hosts `KeeperView` — a native WPF drawing of the app's Keeper
avatar (same paths, same weather-conditional clothing: raincoat, wool hat,
sunglasses, storm blanket, held umbrella) with its breathing, blinking,
fidgets and sky effects as WPF animations. There's no embedded browser:
`WeatherService` fetches the current temperature and WMO weather code from
Open-Meteo on the host and maps it to a sky (clear/cloudy/rain/snow/storm…).
A second, invisible window sized to the avatar takes the clicks — dragging,
the icon bar and the right-click menu. Settings (location, window position,
always-on-top) persist to `%AppData%\CloCloWidget\settings.json`.

## Security

What the widget does to protect itself and what it shows:

- **No browser engine.** Keeper is drawn natively in WPF, so there's no web
  page, script or embedded Chromium to attack. The only network calls are
  HTTPS requests to Open-Meteo (weather, place search) and OpenStreetMap
  Nominatim ("Use my location"), with fixed hosts, a 15-second timeout and a
  1 MB response limit.
- **Location stays coarse.** Coordinates are rounded to about 1 km before
  they're saved or sent anywhere; a precise GPS fix never leaves the machine.
- **Out of screen sharing.** The widget and the clipboard-history and QR
  windows are hidden from screenshots, recordings and screen sharing by
  default (right-click → Privacy). Notification text can be switched off so
  only the sending app's name shows.
- **Careful with your selection.** "Read selection", "Convert" and "QR" ask
  the app for the highlighted text through UI Automation first, without
  touching the clipboard. Only when that isn't supported do they send Ctrl+C,
  and then: never to a terminal (where Ctrl+C stops the running command),
  never to a password field, never to a window that's no longer in front, and
  the previous clipboard is put back afterwards, kept out of clipboard history.
- **Checks its own native files at startup.** The single-file exe unpacks
  WPF's native DLLs to `%TEMP%\.net\CloCloWidget\`, and .NET reuses whatever
  it finds there. Before anything loads them, CloClo checks each one against
  the exact name and SHA-256 recorded inside the signed exe at build time,
  plus an intact Microsoft signature. A planted, modified, older or renamed
  file makes it re-extract clean copies; if that doesn't fix it, it refuses
  to run.
- **Refuses loader tricks.** It won't start while `CloCloWidget.exe.config`,
  `CloCloWidget.exe.local` or `CloCloWidget.exe.manifest` sits next to it —
  Windows reads those before any app code runs, and CloClo never creates
  them. The `DOTNET_STARTUP_HOOKS` variable, which would run another DLL's
  code before the app starts, is ignored.
- **Never runs as administrator by accident.** `install.ps1` starts the
  widget as your normal user even when run from an admin window.
- **Signed builds.** Every DLL bundled into the exe and the exe itself can be
  Authenticode-signed and timestamped (see [Signing](#signing)).

Limits: everything above protects against tampering by other programs, but a
program already running as you can still change the installed exe itself
(`%LocalAppData%\Programs` belongs to your account), or attach a .NET
profiler through environment variables before any app code runs. Closing
that gap needs an install location your account can't write to, such as
Program Files, which needs administrator rights.
