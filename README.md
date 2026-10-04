# CloClo Desktop Widget

A small always-on-top floating widget for Windows: the Keeper avatar from the
CloClo app, dressed for the real current weather at a location you set,
sitting near your taskbar clock (Windows doesn't allow any app to draw
directly on the system clock itself, so this is the closest a normal app can
get — the bottom-right corner, just above the taskbar).

## Getting started

1. **Double-click `Install-CloClo.cmd`** in the `cloclo-widget` folder — as
   yourself, not with "Run as administrator". It builds, signs and installs
   CloClo the recommended way:
   - into `C:\Program Files\CloClo`, where no other program running as you can
     change it (one administrator prompt, for the copy only);
   - signed with your newest CloClo certificate whose key is protected — if
     you have none, it offers to create one, and it never signs with an
     unprotected key;
   - with Start Menu and desktop shortcuts, and listed in **Settings → Apps →
     Installed apps**;
   - recording a fingerprint of the source it was built from, for
     `Check-Source.cmd`.

   Windows asks you to confirm the signing twice (once for the DLLs, once for
   the exe), then shows the administrator prompt. The window stays open at
   the end so you can read the result.
2. CloClo starts by itself afterwards; next time, use the Start Menu or
   desktop shortcut. It appears in the bottom-right corner, just above the
   taskbar.
3. Right-click it → **Set location…** to pick your city.

To remove it, double-click `Uninstall-CloClo.cmd`, or use **Settings → Apps →
Installed apps → CloClo → Uninstall**.

To see whether anything in the project folder changed since the last
install, double-click **`Check-Source.cmd`** — see
[Checking the source](#checking-the-source).

To choose differently (a per-user install, a `.pfx`, no signing…), run
`install.ps1` yourself — see [Installing](#installing).

Always start the installed copy, not `publish\CloCloWidget.exe`: every install
empties and rebuilds `publish\`, and a copy running from there gets in the way.

### Requirements

- **To run:** Windows 10 version 2004 or later, or Windows 11, 64-bit. Nothing
  else — the app carries its own .NET runtime.
- **To build:** the [.NET 10 SDK](https://dotnet.microsoft.com/download),
  installed in its standard location (`C:\Program Files\dotnet`) —
  `install.ps1` only runs `dotnet` from there.
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
  | Read selection aloud | Reads the text you highlighted in another app (up to 5,000 characters) |
  | Dictate | Opens Windows voice typing; the text is saved to a file |
  | Convert units | Converts a highlighted "5 km", "98 F"… (km/mi/m/ft, kg/lb, °C/°F/K) |
  | Color picker | Pick any color on screen; its hex code is copied (Esc cancels) |
  | QR code from selection | Makes a QR code of the highlighted text or link |
  | Volume/mute toggle | Mutes or unmutes the system sound |
  | Clipboard history | Lists Windows' clipboard history (Win+V); click one to copy it again |

  Clicking the label that appears under the icons opens the app it's showing;
  its pause button stops it.
- **Right-click** (or double-click) for the menu: **Set location…**,
  **Weather** (follow the live forecast, or pick a sky by hand), **Icons**,
  **Sleep after** (Keeper dozes off when you're away), **Workout reminder**
  (a stretch-break nudge after a long stretch at the keyboard), **Refresh
  weather**, **Start new dictation file**, **Open dictation folder**,
  **Privacy**, **Always on top**, **Start with Windows** and **Exit**.
- A **tray icon** (near the clock, in the hidden-icons area) has the same
  menu — double-click it to show or hide the widget.
- **Privacy:** the widget, and the clipboard-history and QR windows, are
  hidden from screenshots, recordings and screen sharing by default.
  Notification text can be switched off so only the sending app's name shows.
- **Weather** refreshes every 20 minutes from
  [Open-Meteo](https://open-meteo.com) (no API key, no account).
- **Where things are kept:**
  - Settings: `%AppData%\CloCloWidget\settings.json`
  - Dictation: `%LocalAppData%\CloCloWidget\Dictation`
  - Error log: `%LocalAppData%\CloCloWidget\errors.log`

## Installing

`install.ps1` builds, signs (optional) and installs CloClo with a Start Menu
shortcut; run it again any time to upgrade. `Install-CloClo.cmd` is simply
`install.ps1 -ProgramFiles -UseCloCloCertificate -DesktopShortcut`. All the
options:

```powershell
.\install.ps1                                   # per-user install, unsigned
.\install.ps1 -ProgramFiles                     # install to Program Files instead (recommended)
.\install.ps1 -CertThumbprint <thumbprint>      # sign with a certificate from your store
.\install.ps1 -PfxPath C:\certs\codesign.pfx    # sign with a .pfx (asks for its password)
.\install.ps1 -UseCloCloCertificate             # sign with your newest protected CloClo certificate (offers to create one)
.\install.ps1 -DesktopShortcut                  # also put a shortcut on the desktop
.\install.ps1 -NoLaunch                         # don't start CloClo afterwards
.\install.ps1 -Uninstall                        # removes either kind of install
```

The options combine. If Windows says running scripts is disabled, start it as
`powershell -ExecutionPolicy Bypass -File .\install.ps1` (plus any options) —
that allows this one run without changing any system setting.

Run it from a regular PowerShell window: it refuses an administrator window
(building should never run as admin; it asks for administrator rights itself
when it needs them), and PowerShell ISE can't pass your typing to it, so it
stops with a message there rather than waiting for an answer it can't get.

Every install closes a running CloClo first, so the exe can be replaced, and
moves "Start with Windows" to the installed copy if it was on.

CloClo then appears in **Settings → Apps → Installed apps**, and can be
uninstalled from there. That runs the `uninstall.ps1` installed next to the
exe, which removes the program, its shortcuts and its "Start with Windows"
entry (asking for administrator rights for a Program Files copy) but leaves
your settings and dictation files in AppData. `.\install.ps1 -Uninstall`
from the project folder does the same.

### Per-user or Program Files

| | Per-user (default) | Program Files (`-ProgramFiles`) |
|---|---|---|
| Location | `%LocalAppData%\Programs\CloCloWidget` | `C:\Program Files\CloClo` |
| Administrator prompt | none | one, for the copy only |
| Who can change the installed files | any program running as you | administrators only |
| WPF's native DLLs | inside the exe, unpacked to `%TEMP%` at runtime and checked at startup | next to the exe, in Program Files |

With `-ProgramFiles`, building and signing still run as you (your certificate
is in your own store); only the copy into Program Files is elevated. Each
file's SHA-256 is taken from the build and re-checked inside Program Files —
a file that changed in between is removed and the install stops — and the
per-user copy, if any, is removed. CloClo itself always starts as your normal
user.

To switch back to a per-user install, run `.\install.ps1 -Uninstall` first
(it asks for administrator rights to remove the Program Files copy), then
install without `-ProgramFiles`.

### Signing

With a certificate, `sign.ps1` runs inside the build in two stages, since the
single-file exe carries every DLL inside it:

1. Just before bundling, every DLL that has no signature at all — the app's
   own `CloCloWidget.dll` and unsigned third-party ones such as `QRCoder.dll`
   — is copied to `obj\...\signed\`, the copy is signed, and the bundle is
   built from the signed copies. Microsoft's runtime DLLs keep their own
   signatures, and nothing in the NuGet cache is modified. A DLL whose
   signature no longer verifies (changed after its publisher signed it)
   stops the build instead of being signed over.
2. The finished `CloCloWidget.exe` is signed.

Everything is SHA-256 signed with an RFC 3161 timestamp (so signatures stay
valid after the certificate expires) and checked right after signing; any
failure fails the build.

Which certificates can sign:

- **From your certificate store** (`-CertThumbprint`): only one whose private
  key Windows protects — strong key protection, where Windows asks you to
  confirm (or enter a password) every time something signs with it, or a
  smart card. An unprotected key is refused, since any program running as you
  could sign its own files as CloClo with it.
- **From a `.pfx` file** (`-PfxPath`): protected by its password, which is
  passed through the `SIGN_CERT_PASSWORD` environment variable, never on a
  command line. The certificate is loaded in memory, placed in your personal
  store only while signing, and removed again afterwards with its private key.

Any Authenticode code-signing certificate works; Azure Trusted Signing is the
cheapest option for an individual developer.

**Updates keep the certificate.** The installer only replaces an installed
CloClo with a build signed by the same certificate. If it differs — or the
new build is unsigned and the installed one isn't — it shows both thumbprints
and continues only if you type `YES`. A swapped build signed by someone else
would look exactly like that, so only answer yes when you changed the
certificate yourself.

#### Signing with your own certificate (left untrusted)

A self-signed certificate signs everything exactly like a bought one; the only
difference is that no Windows PC trusts it, so the publisher still shows as
unverified. It stays that way on purpose — it's never added to Windows'
trusted roots.

**The easy way:** double-click `Install-CloClo.cmd`. It uses your newest
certificate named `CN=CloClo…` (for example `CN=CloClo` or
`CN=CloClo_yourname`) whose key is protected, and if there's none it asks
whether to create one — nothing else to type.

**By hand** — the commands below use the name `CN=CloClo`. If yours has
another name, use that name in every command, otherwise loading or deleting
the certificate finds nothing.

**First time — create the certificate** (once; it's valid for five years).
`-KeyProtection Protect` gives it a protected private key; Windows shows a
dialog while creating it, where you pick how each signing is confirmed —
Medium asks you to click OK, High asks for a password:

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=CloClo" -CertStoreLocation Cert:\CurrentUser\My -KeyProtection Protect -NotAfter (Get-Date).AddYears(5)
```

**Every time — sign and install.** In a new PowerShell window, load the
certificate first (this picks the newest one, so it works even if there's
more than one); in the window you just created it in, `$cert` is already set:

```powershell
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Subject -eq "CN=CloClo" | Sort-Object NotBefore -Descending | Select-Object -First 1
```

Then build, sign and install:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -ProgramFiles -CertThumbprint $cert.Thumbprint -DesktopShortcut
```

Windows asks you to confirm the signing twice — once for the DLLs, once for
the exe. The build prints "signed, but the certificate isn't trusted on this
machine" for each file, and the last line reads "signed, but this PC doesn't
trust the certificate yet". Both are expected.

**Replacing the certificate** — for example one made before key protection
was required, which the build now refuses. Delete every CloClo certificate
together with its private key (other certificates aren't touched), then
either double-click `Install-CloClo.cmd` (it offers to create the new one) or
create it and install by hand as below. Either way the installer asks for
`YES` once, because the certificate changed:

```powershell
Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq "CN=CloClo" | ForEach-Object { Remove-Item -Path "Cert:\CurrentUser\My\$($_.Thumbprint)" -DeleteKey }
```

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=CloClo" -CertStoreLocation Cert:\CurrentUser\My -KeyProtection Protect -NotAfter (Get-Date).AddYears(5)
```

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -ProgramFiles -CertThumbprint $cert.Thumbprint -DesktopShortcut
```

Deleting the key is permanent, and already-signed copies keep their
signature. Keep the private key to yourself — never export the certificate
with its key.

## Checking the source

Every install records the SHA-256 of each source file in the project folder
(everything except build output: `bin`, `obj`, `publish`, `.vs`), plus the
git commit, in `source-manifest.json` — taken just before the build and
checked again right after it, so the record matches what was actually built.
The record and its checker, `check-source.ps1`, are installed next to the
program; in Program Files only administrators can change them, so whatever
might change the code can't also rewrite the record.

Double-click **`Check-Source.cmd`** to compare the project folder with the
last install. It runs the *installed* checker and lists every file that was
changed, added or removed since then — or says that all of them still match.
Right after an install it reports no changes; any change you then make
yourself (editing code, `git pull`) shows up until the next install records
the new state.

The installer runs the same check automatically before every build: if the
source changed since the last install, it lists the changed files and only
builds after you type `YES` — so code nobody meant to change can't be built
and signed with your key unnoticed. Run it before building again if you're unsure whether
something touched the code. (With a per-user install the record sits in a
folder you can write to, so the check is weaker; the script says so.)

## Troubleshooting

| What you see | What to do |
|---|---|
| "running scripts is disabled on this system" | Start it as `powershell -ExecutionPolicy Bypass -File .\install.ps1 …` |
| "Run this from a normal PowerShell window" | You're in an administrator window — open a regular one |
| It stops with "Can't ask for YES here" | You're in PowerShell ISE — use Windows PowerShell or Windows Terminal |
| "has an unprotected software key" | Replace the certificate (see [Replacing the certificate](#signing-with-your-own-certificate-left-untrusted)) |
| "This build isn't signed by the same certificate" | Expected once after you change certificates — type `YES`; otherwise stop and find out why |
| "Couldn't close the running CloClo" | A copy was started as administrator — right-click it → Exit, then run the installer again |
| "The .NET SDK wasn't found" | Install the .NET 10 SDK from dotnet.microsoft.com (it installs under Program Files) |
| CloClo says it "won't start while these files are next to it" or "found native files it can't verify" | Something placed files beside or into CloClo — delete the files it names, consider a malware scan, and reinstall |
| CloClo "won't run with a .NET profiler attached" | Remove the listed environment variables (System Properties → Environment Variables), sign out and back in |
| CloClo doesn't start at sign-in | Check **Settings → Apps → Startup** has CloClo switched on; otherwise right-click it → **Start with Windows** off and on again |
| "Source files … changed while building" | Something edited the project during the build — run the install again, and use `Check-Source.cmd` if you didn't expect changes |
| `Check-Source.cmd`: "isn't installed with a source record yet" | The installed copy predates source records — run `Install-CloClo.cmd` once |
| `Check-Source.cmd` lists changes you didn't make | Don't build or install; look at the listed files first (`git diff` shows what changed) |
| The installer lists source changes and asks for `YES` | Expected after you edited code or pulled; if you didn't, answer anything else and check the files |
| Restore fails with NU3004 / NU3018 | A package isn't signed by nuget.org (or nuget.org renewed its certificate) — if it's genuinely from nuget.org, re-run `dotnet nuget trust source nuget.org` with the configfile option pointing at `nuget.config` |

## Rebuilding from source

```bash
dotnet build
dotnet run
```

A plain `dotnet build` is fine for development but isn't the installed
layout: it unpacks nothing, so the startup checks of native files have
nothing to do, and it never touches "Start with Windows". `install.ps1` is
the way to get a real installed copy.

To produce a standalone .exe by hand:

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
the icon bar and the right-click menu.

`Program.Main` runs `NativeLibraryCheck` before WPF loads anything (see
Security below). The build itself, through targets in `CloCloWidget.csproj`,
records the SHA-256 of every native runtime DLL it ships
(`NativeLibraryPins.g.cs`) and, with a certificate, calls `sign.ps1`;
`install.ps1` drives the whole build-sign-install sequence, and
`check-source.ps1` records and checks the source fingerprint. The `.cmd`
files are double-click launchers for those scripts.

## Security

**What CloClo shows and sends**

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
- **Doesn't crash on errors.** Unexpected errors are logged to
  `errors.log` (error type and code location only, never any text) and the
  widget keeps running — a crash would also hand Windows Error Reporting a
  memory dump that could contain clipboard or notification text.

**What CloClo checks before it runs** (in `NativeLibraryCheck`, before WPF
loads anything)

- **Its own native files.** In the per-user install, the single-file exe
  unpacks WPF's native DLLs to `%TEMP%\.net\CloCloWidget\`, and .NET reuses
  whatever it finds there. Each one must match the exact name and SHA-256
  recorded inside the signed exe at build time and carry an intact Microsoft
  signature. A planted, modified, older or renamed file makes CloClo
  re-extract clean copies; if that doesn't fix it, it refuses to run. (A
  Program Files install keeps these DLLs next to the exe instead, where only
  administrators can change them.)
- **No look-alike DLLs beside the exe.** Windows looks in the exe's own
  folder first for many system DLLs it loads by name. Every DLL beside the
  exe must be one of CloClo's pinned files; anything else makes it refuse to
  start. In testing, every DLL name that could be confirmed — 47 of the names
  CloClo loads, including `version.dll`, `dwmapi.dll`, `winmm.dll`,
  `cryptbase.dll`, `cryptsp.dll` and `profapi.dll` — was refused before it
  loaded. CloClo's own calls into Windows DLLs only ever load them from
  System32.
- **No loader tricks.** It won't start while `CloCloWidget.exe.config`,
  `CloCloWidget.exe.local` or `CloCloWidget.exe.manifest` sits next to it —
  Windows reads those before any app code runs, and CloClo never creates
  them. The `DOTNET_STARTUP_HOOKS` variable, which would run another DLL's
  code before the app starts, is ignored.
- **No profilers.** It refuses to run while a .NET profiler is set in the
  environment (`CORECLR_ENABLE_PROFILING` and related variables).
- **"Start with Windows" follows the installed copy.** When an installed
  CloClo starts and the startup entry points to a copy that's gone (or, for
  the Program Files copy, anywhere else), it repoints the entry to itself, so
  it can't be left aimed at a deleted per-user folder that another program
  could fill. Development builds and copies run from elsewhere never touch it.

**How builds and installs are protected**

- **Signed builds** — every unsigned DLL bundled into the exe, and the exe
  itself, timestamped (see [Signing](#signing)).
- **Only a protected key can sign**, and **tampered files are never signed
  over** — a DLL whose signature no longer verifies stops the build.
- **Updates keep the signer** unless you type `YES` after seeing both
  thumbprints.
- **Only signed packages from nuget.org.** `nuget.config` allows nuget.org as
  the only package source, maps every package to it, and requires each
  package to carry nuget.org's repository signature
  (`signatureValidationMode=require`, trusted certificate fingerprints
  included), so an unsigned or re-packed package is refused (tested: error
  NU3004). NuGet checks this when it unpacks a package into its cache;
  packages already in the cache from before were unpacked without it —
  `dotnet nuget locals global-packages --clear` makes everything download
  and get checked again (other projects then re-download too).
- **The source is fingerprinted** at every install, checked unchanged across
  the build, and the record is kept where only administrators can change it
  (Program Files), so `Check-Source.cmd` can tell whether the code changed
  since.
- **Administrator rights only where needed.** `install.ps1` refuses to run
  from an administrator window, so building never runs as admin and the
  widget always starts as your normal user. With `-ProgramFiles` it asks for
  administrator rights for the copy alone, runs that step in a fresh
  PowerShell started by its full System32 path, and lets it load modules only
  from PowerShell's own system folder — a look-alike module planted in your
  Documents folder can't run as administrator. The uninstaller works the
  same way: it elevates only to remove the Program Files copy, and runs from
  Program Files itself.
- **No programs found by name.** The build and installer run `dotnet`,
  PowerShell and `signtool` by their full Program Files / System32 paths and
  find folders through Windows' known-folder lookup, never through `PATH` or
  environment variables another program could change.

**Limits — what a program already running as you can still do**

- **With the per-user install,** replace the installed exe itself, since
  `%LocalAppData%\Programs` belongs to your account — and plant a DLL under
  a name that Windows or .NET loads before any of CloClo's code runs, which
  no check inside the app can catch. Installing to Program Files
  (`-ProgramFiles`) closes both: the exe and its native DLLs are then
  writable only by administrators, and nothing is unpacked to `%TEMP%`.
- **With either install,** attach a .NET profiler through environment
  variables. The runtime loads it before CloClo's own code runs; CloClo then
  notices and refuses to continue, but the profiler's code has already run.
  Your environment variables belong to your account, so no install location
  changes this — and the same goes for your shortcuts and the "Start with
  Windows" entry, which another program could repoint before CloClo next
  starts.
