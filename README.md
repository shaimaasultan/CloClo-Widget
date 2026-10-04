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
`SIGN_CERT_PASSWORD` environment variable, never on a command line. Any
Authenticode code-signing certificate works; Azure Trusted Signing is the
cheapest option for an individual developer.

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
