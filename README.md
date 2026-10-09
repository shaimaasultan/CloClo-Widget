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
- **One at a time.** Only one CloClo runs per Windows sign-in. Starting it
  again (shortcut, Start Menu, the exe) doesn't open a second copy - it shows
  the running one, even if it was hidden from the tray, with "CloClo is
  already running".
- **Top line:** the time and date, in Windows' own formats (12- or 24-hour,
  day/month order), e.g. "9:41 PM · Tue, Oct 6". The sky in words sits with
  the temperature: "12° Clear".
- **Icon bar** under the temperature — up to 7 of these at a time, picked in
  right-click → **Icons**:

  | Icon | What it does |
  |---|---|
  | YouTube / Spotify | Shows what's playing there, and keeps it updated |
  | Notifications | Shows your latest notification; the list button opens Windows' notification panel |
  | Search other apps | Lists apps with media sessions; selecting one watches its media metadata without activating it |
  | GPU info | Shows the graphics cards in this PC |
  | Read selection aloud | Reads the text you highlighted in another app (up to 5,000 characters) |
  | Selection details | Shows word, character and line counts and a short preview of highlighted text; nothing is saved (the fallback briefly copies selected text) |
  | Dictate | Opens Windows voice typing; the text is saved to a file |
  | Convert units | Converts a highlighted "5 km", "98 F"… (km/mi/m/ft, kg/lb, °C/°F/K) |
  | Color picker | Pick any color on screen; its hex code is copied (Esc cancels) |
  | QR code from selection | Makes a QR code of the highlighted text or link |
  | Volume/mute toggle | Mutes or unmutes the system sound |
  | Clipboard history | Lists Windows' clipboard history (Win+V); click one to copy it again |
  | Stopwatch | One click starts it, the next stops it, the next resets it; the time shows top-left, under the battery |
  | Daily quote, word, joke & fact | A speech bubble with today's quote, an English → Arabic word (with an example), a joke and a "did you know" fact; click it for the next one, × or Esc to close. All built in - nothing is fetched |
  | Layer Sudoku (daily game) | Opens the Layer Sudoku game - today's daily puzzle (the same one the HTML version gives, with a streak), or 6×6 to 16×16 at four levels; layer view, method-following hints, notes, undo, timer and pause. Progress is saved |

  Turn on **Selection details** under right-click → **Icons**; the icon bar
  holds up to seven choices at once.

  The search box below the widget finds the actions in the right-click menu,
  including **Windows status**, **Custom reminders**, **Open dictation folder**,
  and the avatar, weather, privacy and icon choices. Suggestions open above
  the box; choose with the mouse or arrow keys and Enter. Esc closes them.
  The right-click menu remains available.

  The label under the icons only displays metadata; its pause button stops
  monitoring. The app picker also only selects a media session to watch and
  never activates the source app.
- **Right-click** (or double-click) for the menu: **Set location…**,
  **Avatar** (Keeper or Lady Keeper, and each one's outfit colour), **Weather** (follow the live forecast, or pick a sky by hand), **Icons**,
  **Sleep after** (Keeper dozes off when you're away), **Workout reminder**
  (a stretch-break nudge after a long stretch at the keyboard), **Windows
  status** (local connection and fixed-drive free space), **Refresh weather**,
  **Start new dictation file**, **Open dictation folder**,
  **Privacy**, **Always on top**, **Start with Windows** and **Exit**.
- A **tray icon** (near the clock, in the hidden-icons area) has the same
  menu — double-click it to show or hide the widget.
- **Privacy:** the widget, and the clipboard-history, QR, selection-details and Windows-status windows, are
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
`powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1` (plus any options) —
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
moves "Start with Windows" to the installed copy if it was on.

CloClo then appears in **Settings → Apps → Installed apps** (an entry in your
own account's registry, for both kinds of install), and can be uninstalled
from there. That runs the `uninstall.ps1` installed next to the
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

With `-ProgramFiles`, building, signing and the registry entries all happen
as you (your certificate is in your own store); only the copy into Program
Files is elevated. That step is `place.ps1`, a fixed script that receives
only data — the `publish\` folder, the SHA-256 of every built file, and a
certificate change you confirmed with `YES`. What the administrator prompt
starts is a short bootstrap, and that's all its **Show more details** shows:
`place.ps1`'s full path and SHA-256 (compare it with
`Get-FileHash .\place.ps1` if you like), the `publish\` folder and every file
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
  command line. The certificate is placed in your personal store only while
  signing, and removed again afterwards with its private key. This is the
  weaker option: during the signing that key has no protection, so another
  program running as you could sign with it unnoticed, and the password sits
  in the build's environment while it runs (the install turns off the build
  servers, so no build process keeps it afterwards). If a build is killed
  mid-signing, the next signing with the same `.pfx` removes the leftover
  certificate. Prefer a protected certificate from your store.

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
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -ProgramFiles -CertThumbprint $cert.Thumbprint -DesktopShortcut
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
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -ProgramFiles -CertThumbprint $cert.Thumbprint -DesktopShortcut
```

Deleting the key is permanent, and already-signed copies keep their
signature. Keep the private key to yourself — never export the certificate
with its key.

### An installer for other PCs (CloClo-Setup.exe)

Double-click **`Build-Setup.cmd`**. It builds CloClo, wraps it in a Windows
Installer package and a setup program, signs all three with your CN=CloClo
certificate (Windows asks you to confirm each signing), and leaves
`dist\CloClo-Setup-<version>.exe` (~50 MB, compressed) with its SHA-256.

That one file installs CloClo on any 64-bit Windows 10/11 PC — no .NET SDK
or source folder needed there:

- Windows' own installer does the work: the administrator prompt names the
  publisher, a failed install rolls back completely, and Settings > Apps gets
  an entry with Uninstall.
- It installs to Program Files\CloClo with a Start menu shortcut, closes a
  running CloClo first, and a newer setup replaces an older install in place.
- The last page has a **Launch** button, which starts CloClo as you, not as
  administrator.
- To uninstall: Settings > Apps > CloClo > Uninstall, or run the setup again
  on a PC that has it - it opens on an Uninstall page. Settings, Sudoku
  progress and dictation files are kept.
- To install without any screens: `CloClo-Setup-<version>.exe /quiet`
  (and `/uninstall /quiet` to remove it).

Send the SHA-256 with the file, so the person installing it can check
(`Get-FileHash`) that they got exactly what you built.

On other PCs your self-signed certificate isn't trusted, so Windows shows the
publisher as unknown and SmartScreen warns before it runs. For a clean
install elsewhere, sign with a publicly trusted code-signing certificate
(Azure Trusted Signing is the cheapest for an individual) and pass it:
`Build-Setup.cmd -CertThumbprint <thumbprint>`.

Use either the setup or `Install-CloClo.cmd` on a PC, not both: they're
separate installs of the same folder. To switch to the setup, first remove
the Install-CloClo.cmd install (Settings > Apps > CloClo > Uninstall).
Uninstalling leaves your settings (%AppData%\CloCloWidget); if "Start with
Windows" was on, turn it off in CloClo before uninstalling.

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
| "running scripts is disabled on this system" | Start it as `powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 …` |
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
- **Careful with your selection.** "Read selection", "Selection details", "Convert" and "QR" first
  ask the app for highlighted text through UI Automation. If the app does not
  expose it, the widget refocuses that app, sends Ctrl+C, reads the copied text,
  and restores the previous clipboard contents. This fallback skips terminals
  and password fields. A temporary copy may still appear in Windows clipboard
  history or cloud sync; restoring the previous contents cannot undo that.
  The spoken text is limited to 5,000 characters; the selection-details preview
  is cleared on close. Text that cannot be selected and copied is unavailable.
- **Doesn't crash on errors.** Unexpected errors are logged to
  `errors.log` (error type and code location only, never any text) and the
  widget keeps running — a crash would also hand Windows Error Reporting a
  memory dump that could contain clipboard or notification text. An error on
  a background thread, which can't be survived, ends the process right after
  logging, before Windows collects a dump.
- **Settings written safely.** `settings.json` is written to a new file with
  a random name (which must not exist yet) and then swapped in, so a crash
  can't corrupt it and nothing can pre-place a file to redirect the write.
  Loading is limited to 64 KiB and JSON depth 16; every setting is checked
  against its supported range or value set before the app uses it.
- **Saved content is data, not code.** Dictation is appended as plain text;
  Sudoku saves are bounded JSON deserialized into fixed data classes and
  validated before use. Neither file is passed to a script host, evaluator,
  shell command or dynamic assembly loader.
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
  data cannot trigger program launches.

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
  (Program Files), so `Check-Source.cmd` can tell whether the code changed
  since. The checker never follows folder links (a link is recorded as a
  link, so adding or retargeting one shows up), never runs git - it reads
  the commit straight from the `.git` folder, because git would read
  settings from files any program running as you can edit, some of which
  name a program for git to start - and `Check-Source.cmd` finds the installed checker through Windows'
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
