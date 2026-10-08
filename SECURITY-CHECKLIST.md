# Security checklist — CloClo Desktop Widget

Every hardening point in CloClo, where it lives and how it was checked. `[x]` = implemented; validation
evidence is stated in the item where available. `[ ]` = still awaiting the real-run confirmation described
(these steps need a Windows administrator or signing prompt). Commit ids show where each point was added.

**How to re-check:** `Check-Source.cmd` (project files unchanged since the last install), the README's
[Security](README.md#security) section, and the tests described in each commit.

---

## 1. What CloClo shows, reads and sends

- [x] **No browser engine:** Keeper is drawn natively in WPF — no web page, script or embedded Chromium; one process instead of seven (`f4c3068`)
- [x] Network calls only to fixed HTTPS hosts (Open-Meteo, OpenStreetMap Nominatim), through one shared app-lifetime client with system TLS validation, redirects and cookies disabled, a 15-second timeout, a 1 MB response limit and 10-minute pooled-connection lifetime (`65ffb7a`, `90e33d8`, `7c9d17b`)
- [x] **Location stays coarse:** coordinates rounded to ~1 km when saved, when loaded from older settings and before any request (`65ffb7a`, `90e33d8`)
- [x] **Out of screen sharing:** the widget and the clipboard-history and QR windows are excluded from screenshots, recordings and screen sharing by default (`WDA_EXCLUDEFROMCAPTURE`), with a Privacy toggle (`5236a1d`)
- [x] Notification text can be switched off so only the sending app's name shows (`5236a1d`)
- [x] Selection read only through UI Automation; no Ctrl+C fallback, synthetic keystrokes, or clipboard access (`9af38cd`)
- [x] "Read selection" refuses password fields (`486804f`)
- [x] Clipboard writes retry briefly and report "Clipboard busy" instead of crashing (`486804f`)
- [x] Read-aloud capped at 5,000 characters (`486804f`)
- [x] Dictation files in `%LocalAppData%\CloCloWidget\Dictation`, not next to the exe; a dictation path outside that folder is never appended to (`5236a1d`)
- [x] Win+H sent with `SendInput`; the input language it switches is restored (`90e33d8`)
- [x] "Open dictation folder" starts Explorer by full path with the folder as an argument (no shell-execute of a path) (`35b2e84`)
- [x] Colour-picker overlay cancels itself when it loses focus or after a minute, so it can never trap the screen (`90e33d8`)
- [x] Notification logos decoded only when PNG (no third-party image codecs) and ignored over 512 KB (`90e33d8`, `35b2e84`)
- [x] **No crash dumps:** unexpected errors are logged to `errors.log` (type and stack only, never the message) and the widget keeps running; a background-thread error ends the process with `TerminateProcess` right after logging, before Windows Error Reporting takes a dump (`35b2e84`, `9b99169`) — tested: without it WerFault starts, with it no WerFault
- [x] **Settings written safely:** to a new random-named file created with `CreateNew`, then swapped in - a crash can't corrupt `settings.json`, and nothing can pre-place a file to redirect the write (`90e33d8`, `9b99169`)
- [x] **Settings loaded with limits and validation:** reads at most 64 KiB, limits JSON depth, validates coordinates, window positions, enum values, icon keys/count, timing values, city text and dictation path; invalid fields fall back to safe defaults
- [x] **Dictation files created with `CreateNew`:** a predictable name can't have a file or link placed there first; a taken name gets a number, and the current file is only appended to while it's still a plain file (`bc20286`) — tested: same-second names, pre-placed files left untouched
- [x] **Sudoku progress saved safely:** `sudoku.json` written like `settings.json` (fresh `CreateNew` file, then swapped in); on load every field is checked - sizes, levels, dates, value ranges, givens against the solution, the solution's validity - and anything that doesn't fit is dropped (`1fd95ac`)
- [x] **Daily content built in:** quotes, words, jokes, facts and the Sudoku game are compiled into the signed exe - no new network connection, no outside text shown (`b5b0c11`, `1fd95ac`)
- [x] Read-aloud clears the media source when playback finishes, fails or is stopped (`1517a3e`)
- [x] Closed tool windows and dialogs, speech completion and the stop action schedule one delayed background collection; the now-playing text and logo are cleared when the label is hidden (`7c9d17b`)
- [x] **No Windows Forms:** the tray icon (`Shell_NotifyIcon`), menus (WPF), screen and power queries are WPF or direct Windows calls - ~23 MB of code no longer shipped (`b10c8bd`) — tray registration confirmed with the shell
- [x] **One CloClo per Windows session:** a session-local named mutex, taken after the start-up checks; a second copy only asks the running one to show itself. Other Windows sessions can run their own copy (`88a660f`) — tested: second copy exits, simultaneous starts leave one, a crash releases the mutex

## 2. Start-up checks (before WPF loads anything)

- [x] Hand-written `Program.Main`; WPF start-up kept in a non-inlined `Run()` so compiling `Main` touches no WPF type before the checks (`80f3a9a`, `ef53574`)
- [x] **Process protections from the first line** (`ProcessHardening`): current folder and PATH out of the DLL search; no DLLs from network shares or low-integrity locations; System32 preferred; legacy injection points off (AppInit_DLLs, global hooks, legacy IMEs, Winsock LSPs) (`9b99169`) — verified on the running process in both layouts
- [x] **Unpacked native DLLs verified:** every DLL in `%TEMP%\.net\CloCloWidget\…` must match the exact name and SHA-256 compiled into the signed exe and carry an intact Microsoft signature under a Microsoft root matched by exact name; a mismatch re-extracts once, then refuses (`80f3a9a`, `ef53574`) — reproduced with a planted DLL first
- [x] **Allow-list for both folders:** beside the exe only CloClo's own files; in the unpack folder only pinned DLLs; any other file or any subfolder refuses the start (or re-extracts) (`5db713b`, `9b99169`) — 47 DLL names Windows/.NET load by name tested, all refused before loading
- [x] **Checked DLLs stay locked** (read sharing only) for the process lifetime, and the hash is read through that same handle (`9b99169`) — tested: write, delete and rename blocked while running
- [x] No `CloCloWidget.exe.config`, `.local` or `.manifest` beside the exe (read by Windows' loader before any app code) (`ef53574`)
- [x] `DOTNET_STARTUP_HOOKS` ignored (`StartupHookSupport=false`) (`ef53574`)
- [x] **No profilers:** refuses to run with `CORECLR_*` / profiler variables set (`975d534`)
  - Note: the runtime has already loaded a profiler when this check runs, so it limits rather than prevents it
- [x] The app's own calls into Windows DLLs load from System32 only (`DefaultDllImportSearchPaths`) (`5db713b`)
- [x] **Never as administrator:** refuses to run elevated while User Account Control is on (`9b99169`) — see section 3
- [x] **Refusals shown before WPF loads:** with Windows' own `MessageBoxW`, so no WPF (or Windows Forms) code is loaded to report a check that failed (`b10c8bd`) — tested with a profiler variable set: native box with the full message, exit 1
- [x] **"Start with Windows" follows the installed copy:** an installed CloClo repoints the entry when it points to a deleted copy (the Program Files copy also when it points anywhere else); development builds never touch it (`00b0d61`) — 9 decision cases tested

## 3. Administrator rights - only for installing, never for CloClo

With User Account Control enabled, administrator rights are used for writing to Program Files (and, for the
setup, Windows' package cache). Building, signing and CloClo run as the signed-in user; only the copy/install
step is elevated.

| What | Runs as | How it's enforced |
|---|---|---|
| `CloCloWidget.exe` - the program | signed-in user's token | Its manifest asks for `asInvoker`; when UAC is enabled it refuses to continue if launched elevated |
| CloClo started by an installer (Launch button, after `Install-CloClo.cmd`) | **you** | The setup's screens - which own Launch - run as you; `install.ps1` starts CloClo after the elevated step has ended |
| Building and signing (`Install-CloClo.cmd`, `Build-Setup.cmd`) | **you** | Both refuse an administrator window when UAC is enabled |
| `Install-CloClo.cmd`'s copy into Program Files | administrator (one UAC prompt) | Only `place.ps1`, read and hash-checked by a short inline bootstrap |
| `CloClo-Setup.exe` | **you**, until you click Install | Its manifest asks for `asInvoker`; the setup screens run as you |
| The setup's install, update or uninstall step | administrator (one UAC prompt) | Only Burn's helper (keeps the signed package in `C:\ProgramData\Package Cache`) and Windows Installer (writes Program Files\CloClo and the all-users Start menu shortcut) |
| Uninstall from Settings › Apps | administrator for removing the Program Files folder | Same steps as above |

- [x] `app.manifest`: `requestedExecutionLevel level="asInvoker" uiAccess="false"` - CloClo never asks for administrator rights
- [x] CloClo refuses to run elevated while UAC is on (`9b99169`)
- [x] The Program Files installers never leave CloClo running as administrator (`2f3b7f9`, `3f70ae5`, `477810a`)
- [x] `CloClo-Setup.exe` starts as you (`asInvoker`, checked in the built exe); the setup screens and Launch run as you; only the install step is elevated (`477810a`) — confirmed in the install log of 1.26.41913: "Product installation will be elevated because user is admin and product is being installed per-machine", after Windows checked the package: "has a digital signature ... is permitted to run"
- [x] A declined prompt (Windows error 1223) and a prompt that couldn't start are reported separately, with Windows' own reason for the second (`30c873f`)

## 4. Builds and signing

- [x] Opt-in Authenticode signing with SHA-256 and RFC 3161 timestamps, checked right after signing (`50adfda`)
- [x] Unsigned bundled DLLs (`CloCloWidget.dll`, `QRCoder.dll`) signed as copies under `obj\`, leaving the NuGet cache untouched; the finished exe signed last (`bda620f`)
- [x] **Tampered files are never signed over:** only DLLs with no signature are signed; a broken signature stops the build before anything is copied or signed (`00b0d61`) — tested with a one-byte-altered runtime DLL
- [x] **Only a protected key can sign:** store certificates need strong key protection (confirmation or password each use) or a smart card; an unprotected key is refused (`975d534`)
- [x] `.pfx` password passed through the environment, never on a command line; the certificate is in the store only while signing (`b537a9c`)
- [x] **No `.pfx` key left behind:** a killed build's leftover certificate and its private key are removed at the next signing with the same `.pfx` (marker in `obj\`; another `.pfx`'s leftover only gets a warning); no build server outlives the install with the password in its environment (`--disable-build-servers -nodeReuse:false`) (`8631357`) — tested with a throwaway `.pfx` and a simulated killed build: certificate, key and marker all gone
- [x] `Install-CloClo.cmd` picks the newest protected `CN=CloClo…` certificate or offers to create one with `-KeyProtection Protect` (`2335cf4`)
- [x] **Pinned DLLs come from verified packages:** `verify-pins.ps1` checks each pinned native DLL against its signed `.nupkg` - signatures, author Microsoft Corporation, repository nuget.org, id/version, byte-for-byte match (`9b99169`) — tested: 19 DLLs pass, a one-byte change fails the build
- [x] **Only signed nuget.org packages** (`nuget.config`: single source, package mapping, `signatureValidationMode=require`) (`22ee3b9`) — tested: unsigned package refused (NU3004)
- [x] **Dependency updates checked weekly:** Dependabot opens update PRs for app NuGet packages, installer NuGet packages and GitHub Actions (`.github/dependabot.yml`)
- [x] `dotnet`, PowerShell and `signtool` run by full Program Files / System32 paths, never through PATH (`00b0d61`, `3f70ae5`)
- [x] Building refuses an administrator window (`3f70ae5`)
- [x] A running widget is closed before building (its exe would be locked) (`668795b`)
- [x] Build warnings fixed rather than hidden; the two suppressed ones are by design and commented (`Assembly.Location` is the single-file test; WPF reads DPI from the manifest) (`47a11af`)
- [x] **`CloClo-Setup.exe` (`Build-Setup.cmd`):** a Windows Installer package and setup program built with WiX Toolset 5.0.2 from nuget.org, under the same signature-required `nuget.config` (`477810a`)
  - CloClo, the package (`CloClo.msi`), the setup's engine and the finished setup are all signed through `sign.ps1` - protected key only, timestamped, verified after signing
  - The package holds only the signed exe and the five native WPF DLLs (checked in the built `.msi`); the exe carries a rising file version so Windows Installer replaces an older one
  - Per-machine into Program Files; a running CloClo is closed first; a failed install rolls back; a newer setup replaces an older install in place
  - `build-setup.ps1` follows `install.ps1`'s hardening (module path first, full paths, refuses an administrator window) and prints the setup's SHA-256 to send along with it
  - Windows Installer validation runs on every build; ICE09, ICE32 and ICE61 are skipped only because they're written in VBScript, which recent Windows 11 no longer includes
  - Installer build output stays under `obj\` and `bin\`; `dist\` is ignored by git and by Check-Source
- [x] Confirmed on a real run: the signed setup installed 1.26.41913 (signature verified by Windows Installer before it ran)

## 5. Install in Program Files

- [x] `-ProgramFiles` installs to `C:\Program Files\CloClo` (administrators only); WPF's native DLLs sit beside the exe instead of being unpacked to `%TEMP%`; `publish\` emptied first; a per-user copy is removed (`c311209`)
- [x] Build, signing and registry entries run as you; **only the copy is elevated** (`c311209`, `090a145`)
- [x] **Readable administrator prompt:** a short bootstrap - `place.ps1`'s path and SHA-256, every file's SHA-256, an accepted certificate - is all "Show more details" shows; elevated, it reads `place.ps1` once, checks the SHA-256 and runs exactly those bytes (`037d7be`, `72c19ec`) — tested: swapped `place.ps1` → exit 8
- [x] **`place.ps1` trusts nothing it is handed:** works out the folder itself; refuses unexpected names and links; stages in `CloClo.new` inside Program Files; checks the exact file set, every SHA-256, the exe signer and every DLL as intact Microsoft-signed; swaps folders atomically with rollback; writes no registry keys and no log files (`090a145`) — 10 cases tested
- [x] **Updates keep the signer:** a different certificate (or unsigned over signed) shows both thumbprints and needs a typed `YES` (`975d534`) — 6 cases tested
- [x] The `YES` question is shown explicitly, and the installer stops instead of hanging when it can't read the keyboard (PowerShell ISE) (`af063d3`, `5dfed53`)
- [x] After the copy, the installer checks as you that the install folder holds exactly the built files (`090a145`)
- [x] The installer never leaves CloClo running as administrator (`2f3b7f9`, `3f70ae5`)
- [x] "Start with Windows" read back after the install, with a warning if it didn't stick (`00b0d61`)
- [x] Installed apps entry (Settings › Apps) written as you, in HKCU; an older HKLM entry is removed (`a171fbe`, `090a145`)
- [ ] Confirm on a real run: `Install-CloClo.cmd` → signing confirmation → UAC prompt ("Show more details" lists `place.ps1` and its SHA-256) → installed and checked

## 6. Script hygiene (installer, signer, checker, uninstaller, admin step)

- [x] **Only PowerShell's own modules:** every script limits `PSModulePath` to `$PSHOME\Modules` before any command (`3f70ae5`, `090a145`)
- [x] **Set with plain text, not a command:** `$PSHOME + '\Modules'` - a `Join-Path` there would itself be looked up first (`9a0853f`) — reproduced the look-alike `Join-Path`, then fixed; re-tested with 31 look-alike commands, none ran
- [x] The elevated step always runs in a fresh PowerShell started by its full System32 path, also when already elevated (`3f70ae5`)
- [x] Program Files, LocalAppData and the Windows Kits folder from known-folder lookups, never environment variables (`3f70ae5`)
- [x] `install.ps1` works when pasted into a console and fails clearly outside the project (`8ce4632`)

## 7. Uninstall

- [x] `uninstall.ps1` is copied (hash-checked) into the install folder, so Settings › Apps can remove CloClo without the project folder (`a171fbe`)
- [x] It removes only what points at its own copy (shortcuts, "Start with Windows", the entry, the folder) (`a171fbe`)
- [x] For Program Files it re-runs itself elevated from Program Files, with module loading limited (`a171fbe`)
- [x] `Uninstall-CloClo.cmd` double-click uninstaller (`2335cf4`)
- [x] A successful uninstall is no longer reported as failed: each removal is conditional and the elevated step ends with `exit 0`, so only a real failure (files in use) stops it (`477810a`) — reproduced the false exit 1, then confirmed exit 0
- [x] **Setup installs:** Settings › Apps › CloClo › Uninstall, or run the setup again - it opens on an Uninstall page; settings, Sudoku progress and dictation files are kept (`477810a`)
- [ ] Confirm on a real run: the setup's Uninstall page
- [ ] Confirm on a real run (only when you want to uninstall)

## 8. Checking the project folder

- [x] **Source record:** the SHA-256 of every project file is recorded before the build and checked again after it; a change while building stops the install (`e2ebb63`)
- [x] The record and the checker are installed beside the program (Program Files: administrators only) (`e2ebb63`)
- [x] **Checked before every build:** the installed checker compares the folder with the last install's record; changes are listed and built only after `YES` (`22ee3b9`)
- [x] `Check-Source.cmd` finds the installed checker through Windows' known-folder lookup, never `%ProgramFiles%` / `%LOCALAPPDATA%` (`9b99169`)
- [x] The checker never follows folder links (a link is recorded as `LINK:<target>`) (`9b99169`)
- [x] **The checker never runs git:** the commit is read from `.git` directly - git would read settings any program running as you can edit, some of which name a program to start (`9a0853f`) — verified it matches `git rev-parse HEAD`
- [x] **Line endings fixed** (`* text=auto eol=crlf`), so a checkout never changes file bytes and causes false alarms (`3540fc7`)
- [ ] After the next install, run `Check-Source.cmd` → "No changes"

---

## Known limits (by design)

- The one-instance mutex is per Windows session, not machine-wide. A second signed-in or Remote Desktop session can run its own widget.
- The elevated-launch refusal relies on UAC being enabled. With UAC disabled, the app can inherit the launching process's token.
- **Per-user install:** any program running as you can replace the installed exe, and plant a DLL under a name
  Windows or .NET loads before any of CloClo's code runs. `-ProgramFiles` closes both.
- **Either install:** a .NET profiler set through environment variables loads before CloClo's code; CloClo then
  refuses to continue, but the profiler's code has already run. Shortcuts and the "Start with Windows" entry
  belong to your account too.
- Windows' administrator prompt is not a security boundary against programs already running as you: they could
  change the project's scripts or sources before you install. Run `Check-Source.cmd` before installing if in
  doubt, and confirm a signing request only while a build you started is running.
- Minor: .NET's debug-dump variables (`DOTNET_DbgEnableMiniDump` and related) could make the runtime write a
  memory dump on a crash; only a program already running as you can set them, and it could read CloClo's
  memory directly anyway.
- Minor: the extension-point policy can stop very old, non-TSF input methods from working in CloClo's text
  boxes (modern Windows input methods are not affected).
- **`.pfx` signing (`-PfxPath`):** while signing, the key sits unprotected in your personal store (signtool can only
  use a key from the store) and the password is in the build's environment; any program running as you could use
  either in that time. A protected store certificate (the default) doesn't have this window.
- **`CloClo-Setup.exe` on other PCs:** signed with your self-signed CN=CloClo certificate, which other PCs don't
  trust - Windows shows the publisher as unknown and SmartScreen warns. Sign with a publicly trusted
  code-signing certificate for others to install cleanly.
- Use either `CloClo-Setup.exe` or `Install-CloClo.cmd` on a PC, not both: they're separate installs of the same
  folder.
- After a setup uninstall, "Start with Windows" (your own HKCU entry) stays if it was on; turn it off in CloClo
  first. Windows keeps a copy of the signed package in `C:\ProgramData\Package Cache` (administrators only) for
  uninstalling.
- Minor: a program running as you could hold the single-instance mutex and keep CloClo from starting - it could
  just as well close CloClo.
