# CloClo Desktop Widget

A small always-on-top floating widget for Windows: the Keeper avatar from the
CloClo app, dressed for the real current weather at a location you set,
sitting near your taskbar clock (Windows doesn't allow any app to draw
directly on the system clock itself, so this is the closest a normal app can
get — the bottom-right corner, just above the taskbar).

## Getting started

1. Install it — from the `cloclo-widget` folder, in PowerShell:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1 -DesktopShortcut
   ```

   This builds CloClo, installs it for your account and adds Start Menu and
   desktop shortcuts. See [Installing](#installing) for signing and for
   installing to Program Files.
2. Start it from the Start Menu or desktop shortcut. It appears in the
   bottom-right corner, just above the taskbar.
3. Right-click it → **Set location…** to pick your city.

Always start the installed copy, not `publish\CloCloWidget.exe`: every install
empties and rebuilds `publish\`, and a copy running from there gets in the way.

### Requirements

- **To run:** Windows 10 version 2004 or later, or Windows 11, 64-bit. Nothing
  else — the app carries its own .NET runtime.
- **To build:** the [.NET 10 SDK](https://dotnet.microsoft.com/download).
- **To sign:** additionally the Windows SDK, which provides `signtool.exe`.

## Using it

- **Drag** anywhere on the widget to move it. It remembers where you leave it.
- **Icon bar** under the temperature — up to 7 of these at a time, picked in
  right-click → **Icons**:

  | Icon | What it does |
  |---|---|
  | YouTube / Spotify | Shows what's playing there, and keeps it updated |
  | Notifications | Shows your latest notification; the list button opens Windows' notification panel |
  | Search other apps | Lists apps playing media and every open app, to watch or switch to |
  | GPU info | Shows the graphics cards in this PC |
  | Read selection aloud | Reads the text you highlighted in another app |
  | Dictate | Opens Windows voice typing; the text is saved to a file |
  | Convert units | Converts a highlighted "5 km", "98 F"… (km/mi/m/ft, kg/lb, °C/°F/K) |
  | Color picker | Pick any color on screen; its hex code is copied |
  | QR code from selection | Makes a QR code of the highlighted text or link |
  | Volume/mute toggle | Mutes or unmutes the system sound |
  | Clipboard history | Lists Windows' clipboard history (Win+V); click one to copy it again |

  Clicking the label that appears under the icons opens the app it's showing;
  its pause button stops it.
- **Right-click** (or double-click) for the menu: **Set location…**,
  **Weather** (follow the live forecast, or pick a sky by hand), **Icons**,
  **Sleep after** (Keeper dozes off when you're away), **Workout reminder**
  (a stretch-break nudge after a long stretch at the keyboard), **Refresh
  weather**, the two dictation items, **Privacy**, **Always on top**, **Start
  with Windows** and **Exit**.
- A **tray icon** (near the clock, in the hidden-icons area) has the same
  menu — double-click it to show or hide the widget.
- **Privacy:** the widget, and the clipboard-history and QR windows, are
  hidden from screenshots, recordings and screen sharing by default.
  Notification text can be switched off so only the sending app's name shows.
- **Dictation** is saved under `%LocalAppData%\CloCloWidget\Dictation`
  (right-click → **Open dictation folder**).
- **Weather** refreshes every 20 minutes from
  [Open-Meteo](https://open-meteo.com) (no API key, no account).
- **Settings** are kept in `%AppData%\CloCloWidget\settings.json`.

## Installing

One command builds, signs (optional), installs and adds a Start Menu
shortcut — run it again any time to upgrade. By default it installs for your
account only, with no administrator rights needed:

```powershell
.\install.ps1                                   # unsigned
.\install.ps1 -CertThumbprint <thumbprint>      # signed, cert from your certificate store
.\install.ps1 -PfxPath C:\certs\codesign.pfx    # signed, cert from a .pfx (asks for its password)
.\install.ps1 -DesktopShortcut                  # also put a shortcut on the desktop
.\install.ps1 -ProgramFiles                     # install to Program Files instead (see below)
.\install.ps1 -Uninstall                        # removes either kind of install
```

If Windows says running scripts is disabled, start it as
`powershell -ExecutionPolicy Bypass -File .\install.ps1` (plus any options) —
that allows this one run without changing any system setting.

It installs to `%LocalAppData%\Programs\CloCloWidget`, closes a running
widget first so the exe can be replaced, and moves "Start with Windows" to the
installed copy if it was on.

### Installing to Program Files

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -ProgramFiles -CertThumbprint $cert.Thumbprint -DesktopShortcut
```

(`$cert` is your signing certificate — see
[Signing with your own certificate](#signing-with-your-own-certificate-left-untrusted)
for how to load it. Leave `-CertThumbprint …` out to install unsigned.)

This installs to `C:\Program Files\CloClo`, which only administrators can
change, so no other program running as you can replace CloClo or its files.
The difference from the per-user install:

- **One administrator prompt.** Building and signing still run as you (your
  certificate is in your own store); only the copy into Program Files asks
  for administrator rights. The widget itself still starts as your normal
  user, never as administrator.
- **Every file is checked after copying.** Each file's SHA-256 is taken from
  the build and re-checked inside Program Files; a file that changed in
  between is removed and the install stops.
- **Nothing is unpacked to `%TEMP%`.** WPF's native DLLs are installed next to
  the exe in Program Files instead of being unpacked at runtime.
- **The per-user copy is removed**, so shortcuts and "Start with Windows" all
  point at the Program Files one.

To go back to a per-user install, run `.\install.ps1 -Uninstall` first (it
asks for administrator rights to remove the Program Files copy), then install
without `-ProgramFiles`.

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

The commands use the name `CN=CloClo`. If you created yours under another
name (say `CN=CloClo_yourname`), use that name in every command below —
otherwise loading or deleting the certificate finds nothing.

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

A plain `dotnet build` runs fine for development but isn't the shipped
layout: it unpacks nothing, so the startup check of native files has nothing
to do. `install.ps1` is the way to get a real installed copy.

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
- **Checks its own native files at startup.** In the per-user install, the
  single-file exe unpacks WPF's native DLLs to `%TEMP%\.net\CloCloWidget\`,
  and .NET reuses whatever it finds there. (A Program Files install puts
  them next to the exe instead, where only administrators can change them.) Before anything loads them, CloClo checks each one against
  the exact name and SHA-256 recorded inside the signed exe at build time,
  plus an intact Microsoft signature. A planted, modified, older or renamed
  file makes it re-extract clean copies; if that doesn't fix it, it refuses
  to run.
- **Refuses loader tricks.** It won't start while `CloCloWidget.exe.config`,
  `CloCloWidget.exe.local` or `CloCloWidget.exe.manifest` sits next to it —
  Windows reads those before any app code runs, and CloClo never creates
  them. The `DOTNET_STARTUP_HOOKS` variable, which would run another DLL's
  code before the app starts, is ignored.
- **No look-alike DLLs beside the exe.** Windows looks in the exe's own
  folder first for several system DLLs it loads by name (`version.dll`,
  `dwmapi.dll`, `winmm.dll`, `cryptbase.dll`, `profapi.dll` — each one
  tested). CloClo refuses to start while any DLL there isn't one of its own
  pinned files, and its own calls into Windows DLLs only ever load them from
  System32.
- **Administrator rights only where needed.** `install.ps1` refuses to run
  from an administrator window, so building never runs as admin and the
  widget always starts as your normal user. With `-ProgramFiles` it asks for
  administrator rights for the copy alone, runs that step in a fresh
  PowerShell started by its full System32 path, and lets it load modules only
  from PowerShell's own system folder — a look-alike module planted in your
  Documents folder can't run as administrator.
- **Doesn't crash on errors.** Unexpected errors are logged to
  `%LocalAppData%\CloCloWidget\errors.log` (error type and code location only,
  never any text) and the widget keeps running.
- **Signed builds.** Every DLL bundled into the exe and the exe itself can be
  Authenticode-signed and timestamped (see [Signing](#signing)).

Limits — what a program already running as you can still do:

- **With the default per-user install,** replace the installed exe itself,
  since `%LocalAppData%\Programs` belongs to your account. Installing to
  Program Files (`-ProgramFiles`, [above](#installing-to-program-files))
  closes this: the exe and its native DLLs are then writable only by
  administrators, and nothing is unpacked to `%TEMP%`.
- **With either install,** attach a .NET profiler by setting environment
  variables (`CORECLR_ENABLE_PROFILING` and friends), which loads code before
  any of CloClo's own runs. Your environment variables belong to your account,
  so no install location changes this. The same goes for your shortcuts and
  the "Start with Windows" entry, which another program could repoint.
