# CloClo Desktop Widget

A small floating widget for Windows with a Keeper or Lady Keeper avatar,
local time and battery information, weather, reminders and quick tools. By
default it stays above other windows near your taskbar clock. Windows doesn't
allow an app to draw directly on the system clock itself, so CloClo sits just
above the taskbar instead.

## Screenshots

These examples show the Lady Keeper avatar in everyday mode and briefly posing
with a camera for a screenshot notification. The outfit, weather, location and
visible icons depend on your settings and current conditions.

| Everyday widget | Screenshot notification pose | Actions pose |
|---|---|---|
| <img src="docs/screenshots/Screenshot2026-10-08234236.png" alt="CloClo widget with Lady Keeper, clock, battery, sunny weather and icon bar" width="280"> | <img src="docs/screenshots/notification-camera.png" alt="Lady Keeper holding a camera while a screenshot notification is shown" width="280"> | <img src="docs/screenshots/Screenshot2026-10-08234415.png" alt="Lady Keeper holding a camera while a screenshot notification is shown" width="280"> |

## Project layout

- `src/Views/` contains the WPF windows, avatar and search interface.
- `src/Services/` contains weather, speech, reminders, calculator, Sudoku and
  other feature logic.
- `src/Platform/` contains Windows integration and process safeguards.
- `installer/` contains the WiX setup projects; `Assets/` contains the app icon.
- `docs/screenshots/`, `docs/security/` and `docs/notes/` hold visual and
  reference material, including the [security checklist](docs/security/SECURITY-CHECKLIST.md).
- `scripts/` contains the double-click `.cmd` launchers and the PowerShell
  build, install, signing and source-check scripts.
- `config/` contains the app manifest. The project file, `nuget.config` and
  Git configuration files remain at the top level so their tools find them.

## Getting started

1. **Double-click `scripts/Install-CloClo.cmd`** from the project folder — as
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
     `scripts/Check-Source.cmd`.

   Windows may ask you to confirm code signing, then shows the administrator
   prompt for the Program Files copy. The window stays open at the end so you
   can read the result.
2. CloClo starts by itself afterwards; next time, use the Start Menu or
   desktop shortcut. It appears in the bottom-right corner, just above the
   taskbar.
3. Right-click it → **Set location…** to pick your city.

To remove it, double-click `scripts/Uninstall-CloClo.cmd`, or use **Settings → Apps →
Installed apps → CloClo → Uninstall**.

To see whether anything in the project folder changed since the last
install, double-click **`scripts/Check-Source.cmd`** — see
[Checking the source](#checking-the-source).

To choose differently (a per-user install or no signing), run
`scripts/install.ps1` yourself — see [Installing](#installing).

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
- **One at a time.** Only one CloClo runs per Windows sign-in. Starting it
  again (shortcut, Start Menu, the exe) doesn't open a second copy - it shows
  the running one, even if it was hidden from the tray, with "CloClo is
  already running".
- **Top line:** the time and date, in Windows' own formats (12- or 24-hour,
  day/month order), e.g. "9:41 PM · Tue, Oct 6". The sky in words sits with
  the temperature: "12° Clear". The battery shows its charge and, when Windows
  provides it, an estimate of time remaining.
- **Location:** type a city under **Set location…**, or choose **Use my
  location** and grant Windows' location permission. The stored and requested
  coordinates are rounded to two decimal places.
- **Avatar:** choose Keeper or Lady Keeper and a separate everyday outfit colour
  for each. Weather changes the sky and clothing; **Weather** can follow the
  live forecast or use a sky you pick. The avatar sleeps after your chosen idle
  interval and wears workout clothes during a stretch-break reminder. The
  **Motion** menu offers full, gentle and still modes. Time of day adds small
  visual details; the clock and weather text adapt to the desktop background.
- **Reminders:** **Workout reminder** offers a fixed activity interval.
  **Custom reminders…** lets you keep up to five messages, each with a
  choice of 15, 30, 45, 60, 90 or 120 minutes, active hours, an on/off switch
  and optional speech. They use your PC's local time and show while the avatar
  is awake; the reminder outfit returns to normal after the message. The optional startup
  greeting speaks the local account name and briefly salutes; turn it off
  under **Privacy**.
- **Scientific calculator:** open it from the search box or right-click menu.
  It supports typed expressions, a keypad, degree/radian mode, trigonometry,
  logarithms, roots, powers, factorials and `Ans`. Calculations use a bounded
  local arithmetic parser, not a scripting engine or network service.
- **Icon bar** under the temperature — up to 7 of these at a time, picked in
  right-click → **Icons**:

  | Icon | What it does |
  |---|---|
  | YouTube / Spotify | Shows what's playing there, and keeps it updated |
  | Notifications | Shows your latest notification; the list button opens Windows' notification panel |
  | Search other apps | Lists current media sessions; selecting one watches its metadata without activating its app |
  | GPU info | Shows the graphics cards in this PC |
  | Read selection aloud | Opens the shared speech editor, adds highlighted text, and reads it from the editor (up to 5,000 characters per reading) |
  | Selection details | Shows word, character and line counts and a short preview of highlighted text; nothing is saved (the fallback briefly copies selected text) |
  | Dictate | Opens the shared speech editor. **Dictate** uses the default microphone and bundled local Whisper model; **Read aloud** speaks the editor's text; **Transcribe playing audio** captures the default speaker output with the same local model. Load a text file (up to 256 KiB), explicitly save text to the private dictation file, or open that folder from the window. Capture continues until its toggle is turned off or the window closes |
  | Convert units | Converts a highlighted "5 km", "98 F"… (km/mi/m/ft, kg/lb, °C/°F/K) |
  | Color picker | Pick any color on screen; its hex code is copied (Esc cancels) |
  | QR code from selection | Makes a QR code of the highlighted text or link |
  | Volume/mute toggle | Mutes or unmutes the system sound |
  | Clipboard history | Lists Windows' clipboard history (Win+V); click one to copy it again |
  | Stopwatch | One click starts it, the next pauses it, the next stops and resets it; the time shows top-left, under the battery |
  | Daily quote, word, joke & fact | A speech bubble with today's quote, an English → Arabic word (with an example), a joke and a "did you know" fact; click it for the next one, × or Esc to close. All built in - nothing is fetched |
  | Layer Sudoku (daily game) | Opens the Layer Sudoku game - today's daily puzzle (the same one the HTML version gives, with a streak), or 6×6 to 16×16 at four levels; layer view, method-following hints, notes, undo, timer and pause. Progress is saved |

  Turn on **Selection details** under right-click → **Icons**; the icon bar
  holds up to seven choices at once.

  The search box below the widget finds right-click menu actions, including
  **Windows status**, **Scientific calculator**, **Custom reminders**,
  **Open dictation folder**, and avatar, weather and privacy settings.
  Suggestions open below or beside the box; choose with the mouse or arrow
  keys and Enter. Esc closes them. Coloured badges distinguish windows,
  menus, watchers, file actions and settings; toggles show their current state.
  Icon-bar actions such as **Read selection aloud**, **Convert units**,
  **Clipboard history** and **Layer Sudoku** remain searchable when their
  icons are hidden. To change which icons appear, search for **icon** plus
  the tool name (for example, **icon stopwatch**). A plain **stopwatch**
  search runs the stopwatch; its badge shows the next action: green **Start**,
  amber **Pause** or red **Stop**.
  The right-click menu remains available. Notification watching also gives the
  avatar a brief camera, newspaper, maintenance, ChatGPT or Claude pose when
  the sender and title match those categories and motion is enabled. It does
  not read notifications until you choose **Notifications** from the icon bar
  or search.

  The label under the icons only displays metadata; its pause button stops
  monitoring. The app picker also only selects a media session to watch and
  never activates the source app.
- **Right-click** (or double-click) for the menu: **Set location…**, **Avatar**,
  **Weather**, **Icons**, **Motion**, **Sleep after**, **Workout reminder**,
  **Custom reminders…**, **Windows status** (local connection and fixed-drive
  free space), **Scientific calculator…**, **Refresh weather**, **Start new
  dictation file**, **Open dictation folder**, **Privacy**, **Always on top**,
  **Start with Windows** and **Exit**.
- A **tray icon** (near the clock, in the hidden-icons area) has the same
  menu. Double-click it to show or hide the widget. If the widget is hidden,
  right-click the tray icon and choose **Show widget**, or launch CloClo again.
- **Privacy:** by default, CloClo requests Windows capture exclusion for the
  widget and several tool windows that show private content, including
  clipboard history, QR codes, selection details, Windows status and the
  calculator. This helps with supported Windows capture paths; it is not a
  guarantee against every recorder or a camera pointed at the screen.
  Notification text can be switched off so only the sending app's name shows.
- **Weather** refreshes every 20 minutes from
  [Open-Meteo](https://open-meteo.com) (no API key, no account).
- **Where things are kept:**
  - Settings: `%AppData%\CloCloWidget\settings.json`
  - Sudoku progress: `%AppData%\CloCloWidget\sudoku.json`
  - Dictation: `%LocalAppData%\CloCloWidget\Dictation`
  - Error log: `%LocalAppData%\CloCloWidget\errors.log`

## Installing

`scripts/install.ps1` builds, signs (optional) and installs CloClo with a Start Menu
shortcut; run it again any time to upgrade. `scripts/Install-CloClo.cmd` is simply
`scripts/install.ps1 -ProgramFiles -UseCloCloCertificate -DesktopShortcut`. All the
options:

```powershell
.\scripts\install.ps1                                   # per-user install, unsigned
.\scripts\install.ps1 -ProgramFiles                     # install to Program Files instead (recommended)
.\scripts\install.ps1 -CertThumbprint <thumbprint>      # sign with a certificate from your store
.\scripts\install.ps1 -UseCloCloCertificate             # sign with your newest protected CloClo certificate (offers to create one)
.\scripts\install.ps1 -DesktopShortcut                  # also put a shortcut on the desktop
.\scripts\install.ps1 -NoLaunch                         # don't start CloClo afterwards
.\scripts\install.ps1 -Uninstall                        # removes either kind of install
```

The options combine. If Windows says running scripts is disabled, start it as
`powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1` (plus any options) —
that allows this one run without changing any system setting. Keep
`-NoProfile` in it: your PowerShell profile is a script any program running
as you can edit, and without it that script would run first, inside the
installer.

Run it from a regular PowerShell window: it refuses an administrator window
(building should never run as admin; it asks for administrator rights itself
when it needs them — unless User Account Control is switched off, when every
window is an administrator one), and PowerShell ISE can't pass your typing to it, so it
stops with a message there rather than waiting for an answer it can't get.

Every install closes a running CloClo first, so the exe can be replaced, and
enables "Start with Windows" for the current user on a first install. Upgrades
keep the user's on/off choice and move an enabled entry to the installed copy.
At sign-in, CloClo waits for Defender, Windows Security, and Security Center
services to report running before showing the widget. If those services are
unavailable, it starts after at most 90 seconds; opening CloClo manually is
immediate. CloClo does not change security settings or require administrator
rights to run.

CloClo then appears in **Settings → Apps → Installed apps** (an entry in your
own account's registry, for both kinds of install), and can be uninstalled
from there. That runs the `uninstall.ps1` installed next to the
exe, which removes the program, its shortcuts and its "Start with Windows"
entry and its Windows Startup apps record (asking for administrator rights for
a Program Files copy) but leaves
your settings and dictation files in AppData. `.\scripts\install.ps1 -Uninstall`
from the project folder does the same.

### Per-user or Program Files

| | Per-user (default) | Program Files (`-ProgramFiles`) |
|---|---|---|
| Location | `%LocalAppData%\Programs\CloCloWidget` | `C:\Program Files\CloClo` |
| Administrator prompt | none | one, for the copy only |
| Who can change the installed files | any program running as you | administrators only |
| WPF's native DLLs | inside the exe, unpacked to `%TEMP%` at runtime and checked at startup | next to the exe, in Program Files |

With `-ProgramFiles`, building, signing and the registry entries all happen
as you (your certificate is in your own store); only the copy into Program
Files is elevated. That step is `place.ps1`, a fixed script that receives
only data — the `publish\` folder, the SHA-256 of every built file, and a
certificate change you confirmed with `YES`. What the administrator prompt
starts is a short bootstrap, and that's all its **Show more details** shows:
`place.ps1`'s full path and SHA-256 (compare it with
`Get-FileHash .\scripts\place.ps1` if you like), the `publish\` folder and every file
with its SHA-256. As administrator, the bootstrap reads `place.ps1` once,
checks it against that SHA-256 and runs exactly those bytes from memory, so a
`place.ps1` swapped after the installer checked it never runs. Running as
administrator, `place.ps1` trusts nothing it's handed:

- it works out `C:\Program Files\CloClo` itself, so nothing passed in decides
  what is created or removed;
- it refuses files with unexpected names and files that are links;
- it copies everything into a staging folder (`CloClo.new`) inside Program
  Files, where only administrators can change it, and checks there that it
  holds exactly the expected files, each with exactly its built SHA-256, the
  exe signed by the same certificate as the installed copy (or the one you
  accepted), and every DLL intact and signed by Microsoft;
- only then does it swap the folders. A swap fails as a whole while the old
  copy is in use, and on any failure the previous copy is put back — CloClo
  is never left half-installed.

If it refuses, the installer says why ("Not installed: …"). Afterwards it
checks, as you, that Program Files holds exactly the files that were built,
and removes the per-user copy, if any. CloClo itself always starts as your
normal user.

To switch back to a per-user install, run `.\scripts\install.ps1 -Uninstall` first
(it asks for administrator rights to remove the Program Files copy), then
install without `-ProgramFiles`. If the Program Files copy was installed by
`CloClo-Setup.exe`, uninstall it from Settings > Apps first.

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

File-based `.pfx` signing is not supported. It temporarily imported an
unprotected key into the user's certificate store, allowing another process
running as that user to sign during the build. Use a protected store key.

An Authenticode code-signing certificate can also come from a trusted signing
provider; check that provider's current availability and terms before choosing
one.

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

**The easy way:** double-click `scripts/Install-CloClo.cmd`. It uses your newest
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
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -ProgramFiles -CertThumbprint $cert.Thumbprint -DesktopShortcut
```

Windows may ask you to approve signing while it handles the DLLs and finished
exe. The build prints "signed, but the certificate isn't trusted on this
machine" for each file, and the last line reads "signed, but this PC doesn't
trust the certificate yet". Both are expected.

**Replacing the certificate** — for example one made before key protection
was required, which the build now refuses. Delete every CloClo certificate
together with its private key (other certificates aren't touched), then
either double-click `scripts/Install-CloClo.cmd` (it offers to create the new one) or
create it and install by hand as below. Either way the installer asks for
`YES` once, because the certificate changed:

```powershell
Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq "CN=CloClo" | ForEach-Object { Remove-Item -Path "Cert:\CurrentUser\My\$($_.Thumbprint)" -DeleteKey }
```

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=CloClo" -CertStoreLocation Cert:\CurrentUser\My -KeyProtection Protect -NotAfter (Get-Date).AddYears(5)
```

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -ProgramFiles -CertThumbprint $cert.Thumbprint -DesktopShortcut
```

Deleting the key is permanent, and already-signed copies keep their
signature. Keep the private key to yourself — never export the certificate
with its key.

### An installer for other PCs (CloClo-Setup.exe)

Double-click **`scripts/Build-Setup.cmd`**. It builds CloClo, wraps it in a Windows
Installer package and a setup program, signs all three with your CN=CloClo
certificate (Windows asks you to confirm each signing), and leaves
`dist\CloClo-Setup-<version>.exe` (compressed; size varies by build) and a
matching `CloClo-Setup-<version>.txt` build log containing that exact EXE's
SHA-256. Signing approvals happen in Windows dialogs and are not logged.

That one file installs CloClo on any 64-bit Windows 10/11 PC — no .NET SDK
or source folder needed there:

- Windows' own installer does the work: the administrator prompt names the
  publisher, a failed install rolls back completely, and Settings > Apps gets
  an entry with Uninstall.
- It installs to Program Files\CloClo with Start Menu and public desktop
  shortcuts, enables "Start with Windows" for the installing user, closes a
  running CloClo first, and a newer setup replaces an older install in place.
  The sign-in launch waits for the Windows security services as described
  above. The widget's menu can turn startup off again.
- The last page has a **Launch** button, which starts CloClo as you, not as
  administrator.
- To uninstall: Settings > Apps > CloClo > Uninstall, or run the setup again
  on a PC that has it - it opens on an Uninstall page. Settings, Sudoku
  progress and dictation files are kept. MSI removes the startup entry it
  installed; Windows may retain its separate Startup apps approval record.
- To install without any screens: `CloClo-Setup-<version>.exe /quiet`
  (and `/uninstall /quiet` to remove it).

Send the SHA-256 with the file, so the person installing it can check
(`Get-FileHash`) that they got exactly what you built.

On other PCs your self-signed certificate isn't trusted, so Windows shows the
publisher as unknown and SmartScreen warns before it runs. For a clean
install elsewhere, sign with a publicly trusted code-signing certificate
and pass it:
`.\scripts\Build-Setup.cmd -CertThumbprint <thumbprint>`.

If a script-based CloClo installation is registered for your account or PC, uninstall
it from Settings > Apps > Installed apps before running the setup. The setup
shows this instruction and disables Install until you close and reopen it after
removing the old installation. Direct MSI and silent installs also refuse to
install while the script registration remains. A newer MSI setup replaces an
older MSI setup through Windows Installer. The script installer also refuses
to copy over an MSI-managed installation until it is uninstalled.
Uninstalling leaves your settings (%AppData%\CloCloWidget) and dictation files.

## Checking the source

Every install records the SHA-256 of each source file in the project folder
(everything except build output: `bin`, `obj`, `publish`, `.vs`), plus the
git commit, in `source-manifest.json` — taken just before the build and
checked again right after it, so the record matches what was actually built.
The record and its checker, `check-source.ps1`, are installed next to the
program; in Program Files only administrators can change them, so whatever
might change the code can't also rewrite the record.

Double-click **`scripts/Check-Source.cmd`** to compare the project folder with the
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
| "running scripts is disabled on this system" | Start it as `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 …` |
| "Run this from a normal PowerShell window" | You're in an administrator window — open a regular one |
| It stops with "Can't ask for YES here" | You're in PowerShell ISE — use Windows PowerShell or Windows Terminal |
| "has an unprotected software key" | Replace the certificate (see [Replacing the certificate](#signing-with-your-own-certificate-left-untrusted)) |
| "This build isn't signed by the same certificate" | Expected once after you change certificates — type `YES`; otherwise stop and find out why |
| "Not installed: …" after the administrator prompt | The reason is in the message (a broken signature, another certificate, a non-Microsoft DLL, a changed or unexpected file, or the copy in use). Nothing was changed; fix that and run the install again |
| "Couldn't close the running CloClo" | A copy was started as administrator — right-click it → Exit, then run the installer again |
| "The .NET SDK wasn't found" | Install the .NET 10 SDK from dotnet.microsoft.com (it installs under Program Files) |
| CloClo says it "won't start while these files are next to it" or "found native files it can't verify" | Something placed files beside or into CloClo — delete the files it names, consider a malware scan, and reinstall |
| CloClo "won't run with a .NET profiler attached" | Remove the listed environment variables (System Properties → Environment Variables), sign out and back in |
| CloClo doesn't start at sign-in | Check **Settings → Apps → Startup** has CloClo switched on; otherwise right-click it → **Start with Windows** off and on again |
| "Source files … changed while building" | Something edited the project during the build — run the install again, and use `scripts/Check-Source.cmd` if you didn't expect changes |
| `scripts/Check-Source.cmd`: "isn't installed with a source record yet" | The installed copy predates source records — run `scripts/Install-CloClo.cmd` once |
| `scripts/Check-Source.cmd` lists changes you didn't make | Don't build or install; look at the listed files first (`git diff` shows what changed) |
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
`-p:SignCertThumbprint=<thumbprint>` for a protected store certificate.

## How it's built

A WPF host window (transparent, borderless, topmost by default, no taskbar
entry, click-through) hosts `KeeperView` — a native WPF drawing of the app's
Keeper or Lady Keeper avatar with weather, sleep, workout and custom-reminder
outfits, time-of-day details, notification poses and adjustable motion. There
is no embedded browser. `WeatherService` fetches the current temperature and
WMO weather code from Open-Meteo on the host and maps it to a sky
(clear/cloudy/rain/snow/storm…).
A second, invisible window sized to the avatar takes the clicks — dragging,
the icon bar, search box and the right-click menu. The calculator evaluates a
fixed arithmetic grammar in memory; text entered there cannot run commands.

`Program.Main` runs `NativeLibraryCheck` before WPF loads anything (see
Security below). The build itself, through targets in `CloCloWidget.csproj`,
records the SHA-256 of every native runtime DLL it ships
(`NativeLibraryPins.g.cs`) and, with a certificate, calls `scripts/sign.ps1`;
`scripts/install.ps1` drives the whole build-sign-install sequence, and
`scripts/check-source.ps1` records and checks the source fingerprint. The
`.cmd` files in `scripts/` are double-click launchers for those scripts.

## Security

**What CloClo shows and sends**

- **No browser engine.** Keeper is drawn natively in WPF, so there's no web
  page, script or embedded Chromium to attack. The only network calls are
  HTTPS requests to Open-Meteo (weather, place search) and OpenStreetMap
  Nominatim ("Use my location"), with fixed hosts, a 15-second timeout and a
  1 MB response limit.
- **Location stays coarse.** Coordinates are rounded to about 1 km before
  they're saved or sent anywhere; a precise GPS fix never leaves the machine.
- **Capture exclusion request.** By default, CloClo asks Windows to exclude
  the widget and selected tool windows from supported capture APIs
  (right-click → Privacy). [Windows documents this as a capture aid](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity),
  not a security boundary or a guarantee against every way to record a screen.
  Notification text can be switched off so only the sending app's name shows.
- **Careful with your selection.** "Read selection", "Selection details", "Convert" and "QR" first
  ask the app for highlighted text through UI Automation. If the app does not
  expose it, the widget refocuses that app, sends Ctrl+C, reads the copied text,
  and restores the previous clipboard contents. This fallback skips terminals
  and password fields. A temporary copy may still appear in Windows clipboard
  history or cloud sync; restoring the previous contents cannot undo that.
  The spoken text is limited to 5,000 characters; the selection-details preview
  is cleared on close. The shared speech editor reads its own text; the read
  icon adds selected text to that editor. **Dictate** captures the default
  microphone; **Transcribe playing audio** captures all sound on the default
  output device, including other apps and system sounds. Only one capture
  toggle can run at a time. Audio stays in a bounded memory queue for local
  recognition and is discarded when capture stops. Both use Whisper's CPU
  runtime phrase by phrase: audio is split at natural pauses (at most 15
  seconds, cut at the quietest moment so words stay whole), silence before
  speech is skipped, and capture continues until turned off or the editor
  closes. Dictation uses beam search, and Arabic dictation gives Whisper a
  short Arabic (Egyptian for ar-EG) example sentence to steer script and
  spelling. Recognized text is tidied locally: Arabic punctuation after Arabic
  words, no tatweel or doubled diacritics, and segments Whisper commonly
  invents from silence or music (subtitle credits such as "ترجمة نانسي قنقر",
  "Thanks for watching", or "[Music]") are dropped. With Arabic selected the
  editor lays text out right to left; no direction marks are added to it. The mic mode shows the selected input device and requests
  Windows' local communications processing where that device supports it;
  otherwise it uses ordinary mic capture. An optional microphone noise toggle
  applies a local high-pass filter and soft gate to reduce rumble and noise
  between phrases (gentler for Arabic, and held open briefly after speech so
  quiet word endings are kept); it is off by default and cannot separate overlapping voices
  or TV audio. A separate comparison toggle requests 24-bit, 48 kHz mono
  microphone capture and resamples it locally to Whisper's 16 kHz input; the
  default microphone mode remains 16-bit, 16 kHz. The editor displays the
  capture device and format so the same speech can be compared in each mode.
  Playback audio is usually cleaner
  than a microphone recording, so accuracy can still differ. Local
  transcription requires Windows 11, the Visual C++ 2022 x64 runtime,
  and an AVX2/FMA/F16C-capable CPU; the other widget features retain their
  existing Windows 10 support.
  The multilingual base model is included as `ggml-base.bin` beside the
  installed executable, so microphone and playback transcription work offline after install.
  Its size and SHA-256 are checked before use. The verified model bytes are kept
  in memory during transcription so the native recognizer reads exactly what
  CloClo checked. The model and capture
  queue are released when transcription stops; this mode temporarily uses about
  142 MiB more memory than loading the model by pathname. Once loaded, the small
  native runtime DLLs stay loaded until CloClo exits.
  The four native runtime DLLs are pinned by SHA-256 in the executable and
  signed alongside CloClo in signed builds. Audio is not sent to a service.
  **Voice activation** is off by default. Enable it from Privacy in the right-
  click menu or the search action. While enabled, CloClo keeps the microphone
  and its bundled local Whisper model active to listen for "CloClo". Say
  "CloClo", then "CloClo open Notepad now", "CloClo open Paint now",
  "CloClo open OneDrive now", "CloClo open Snipping Tool now",
  "CloClo open Settings now", "CloClo open YouTube now",
  "CloClo open Spotify now", or "CloClo open Word now".
  With **Voice commands in Arabic** turned on in Privacy, recognition is
  steered toward Arabic and the same fixed commands can be said in Arabic,
  for example "كلوكلو افتح المفكرة دلوقتي" or "كلوكلو وقف الصوت دلوقتي"
  ("دلوقتي" or "الآن" ends a command). English phrases still match.
  Say "CloClo stop voice now" to turn voice
  activation off and release the microphone; CloClo then says "Okay, stopping
  voice now" with its local Windows voice. A complete phrase also works
  without a separate wake phrase. For app commands, "now" ends the command
  and returns CloClo to wake listening after confirmation. The speech editor
  pauses this listening while open and
  it resumes when closed. Turning the Privacy switch off stops microphone
  capture. If capture stops unexpectedly, CloClo keeps the Privacy choice on
  and retries locally with a delay of 2 to 60 seconds. Voice commands are
  recognized after a short pause following speech;
  continuous sound is bounded by an eight-second audio chunk. Audio and
  recognized words stay in memory and are not added to the dictation editor.
  Keeper's collar pin is blue while wake listening, pulses after the wake
  phrase, shows amber dots while handling a command, and briefly shows a green
  check or amber exclamation after the result. It disappears when voice
  activation is off; reduced-motion mode keeps these cues static.
  Only fixed app-name phrases are accepted. CloClo says "Okay, opening [app]
  now" with the local English Windows voice after recognizing the phrase,
  then asks for confirmation before opening the matched app. Notepad, Paint,
  Snipping Tool and Settings use fixed Microsoft package identities; Spotify
  uses its fixed Store package identity. OneDrive and Word must be Microsoft-
  signed executables at fixed Program Files paths. YouTube opens a fixed HTTPS
  address in a Microsoft-signed Edge executable from Program Files. The raw transcript is never a path,
  argument, URI, or shell command. Declining the confirmation opens nothing.
  The model is tracked with Git LFS; clone the project with Git LFS enabled
  before building an installer. The installed package is about 142 MiB larger.
  Text that cannot be selected and copied is unavailable to the selection reader.
  Loading a file reads text only from the user-chosen file, with a 256 KiB
  limit; saving still writes only to CloClo's private dictation folder.
- **Error handling.** Unexpected UI-thread errors are logged to `errors.log`
  (exception type and stack trace, not the exception message) and the widget
  attempts to continue. An unhandled background-thread error ends the process
  after logging, before Windows Error Reporting can collect a dump containing
  clipboard or notification text.
- **Settings written safely.** `settings.json` is written to a new file with
  a random name (which must not exist yet) and then swapped in, so a crash
  can't corrupt it and nothing can pre-place a file to redirect the write.
  Loading is limited to 64 KiB and JSON depth 16; every setting is checked
  against its supported range or value set before the app uses it.
- **Saved content is data, not code.** Dictation is appended as plain text;
  Sudoku saves are bounded JSON deserialized into fixed data classes and
  validated before use. Neither file is passed to a script host, evaluator,
  shell command or dynamic assembly loader. Calculator expressions use only
  a fixed set of arithmetic operators and functions, with a 256-character
  limit and no file, process or network access.
- **Startup greeting stays local.** The optional welcome uses Windows' local
  speech synthesis and the local account's full name when one is set. Domain
  accounts use the short logon name without contacting a domain controller. Names are filtered and
  never stored. Turn it off under right-click → Privacy if speaking names
  aloud isn't appropriate.
- **No global input hooks.** The tray icon uses a WPF message callback on its
  own hidden window for tray clicks and Explorer restarts. It is not a global
  keyboard or mouse hook, and it accepts callbacks only for CloClo's tray ID.
  “Open dictation folder” opens Explorer after an explicit click. The media
  picker and its label do not activate other apps; saved dictation and Sudoku
  data cannot trigger program launches. The opt-in voice activation setting can
  open one of eight fixed destinations only after confirmation.

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
- **Nothing unexpected beside the exe or in the unpack folder.** Windows
  looks in the exe's own folder first for many system DLLs it loads by name.
  Both folders are checked against an allow-list: beside the exe only
  CloClo's own files (the exe, its pinned DLLs, the uninstaller, the source
  checker and its record), in the unpack folder only the pinned DLLs. Any
  other file or any subfolder makes CloClo refuse to start (or, in the unpack
  folder, re-extract). In testing, every DLL name that could be confirmed — 47 of the names
  CloClo loads, including `version.dll`, `dwmapi.dll`, `winmm.dll`,
  `cryptbase.dll`, `cryptsp.dll` and `profapi.dll` — was refused before it
  loaded. CloClo's own calls into Windows DLLs only ever load them from
  System32.
- **Checked DLLs stay locked.** Every DLL that passes is held open, shared
  for reading only, for as long as CloClo runs: Windows can load it, but
  nothing can change, rename or delete it between the check and WPF loading
  it — and its hash is read through that same handle.
- **Process protections from the first line.** Before anything else,
  CloClo takes the current folder and `PATH` out of its DLL search, refuses
  DLLs from network shares and low-integrity locations, prefers System32's
  copy of any system DLL, and turns off the legacy ways other programs push
  DLLs into every process (AppInit_DLLs, global window hooks, legacy IMEs,
  Winsock layered providers).
- **Never as administrator.** CloClo refuses to run elevated (unless User
  Account Control is off) — a widget that reads other apps' text and sends
  keystrokes has no business holding those rights.
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
- **Pinned DLLs come from verified packages.** Before the build records the
  SHA-256 of the native runtime DLLs it ships, `verify-pins.ps1` checks each
  one against its NuGet package file: the package's signatures must verify
  (author Microsoft Corporation, repository nuget.org), its own id and
  version must match its cache folder, and every DLL must be byte-for-byte
  its entry inside the package. The unpacked NuGet cache is writable by any
  program running as you, so a changed DLL there would otherwise be pinned
  as genuine.
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
- **Dependency update checks.** Dependabot checks the app and installer NuGet
  packages and GitHub Actions weekly, and opens pull requests for available
  updates.
- **The source is fingerprinted** at every install, checked unchanged across
  the build, and the record is kept where only administrators can change it
  (Program Files), so `scripts/Check-Source.cmd` can tell whether the code changed
  since. The checker never follows folder links (a link is recorded as a
  link, so adding or retargeting one shows up), never runs git - it reads
  the commit straight from the `.git` folder, because git would read
  settings from files any program running as you can edit, some of which
  name a program for git to start - and `scripts/Check-Source.cmd` finds the installed checker through Windows'
  known-folder API rather than environment variables. `.gitattributes` fixes
  every text file's line endings, so a checkout never changes file bytes and
  shows up as changes nobody made.
- **Administrator rights only where needed.** `install.ps1` refuses to run
  from an administrator window, so building never runs as admin and the
  widget always starts as your normal user. With `-ProgramFiles` it asks for
  administrator rights for the copy alone: `place.ps1`, run inline as
  readable PowerShell in a fresh PowerShell started by its full System32
  path. It stages and checks everything inside Program Files, re-checks the
  signatures itself, swaps folders atomically with rollback, and writes no
  registry keys — those are made as you, in your own account.
- **No look-alike PowerShell modules.** Every script (installer, signer,
  source checker, uninstaller and the administrator step) loads modules only
  from PowerShell's own system folder, so a fake `Start-Process` or
  `Get-FileHash` planted in your Documents folder can't change what the UAC
  prompt starts, fake a hash, or run as administrator; the documented
  commands use `-NoProfile` so your PowerShell profile doesn't run first.
  The restriction is set with plain text (`$PSHOME + '\Modules'`), never
  with a command such as `Join-Path` - looking that command up would itself
  happen before the restriction applies (reproduced with a look-alike
  `Join-Path`). The uninstaller works the
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
