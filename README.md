# Steam LZX Tray

A Windows 11 tray tool that compresses your Steam libraries (and any custom folders you add) in the background using **WOF LZX**, the same compression as `compact /c /exe:lzx`. It is not NTFS LZNT1.

Single self-contained exe (`SteamLZXTray.exe`, Windows 10/11 x64, .NET 8 runtime bundled, nothing else to install). It is unsigned, so SmartScreen may warn on first launch (More info → Run anyway).

## Download
Get the latest build from **[Releases](https://github.com/bstone108/SteamLZXTray/releases/latest)**:
- `SteamLZXTray.exe`: the app. Run it once to install.
- `SteamLZXTray-<version>-win-x64.zip`: the same exe plus README and LICENSE.
- `SHA256SUMS.txt`: checksums. Check with `certutil -hashfile SteamLZXTray.exe SHA256`.

## Install, update, uninstall
- **Install or update**: run the downloaded `SteamLZXTray.exe` from anywhere, such as the Desktop. A copy that is **not** at `%LOCALAPPDATA%\Programs\SteamLZXTray\SteamLZXTray.exe` never starts compressing. It only acts as the installer:
  1. Signals a running instance, through a named event, to save its state and exit. If that instance hasn't exited after about 10 s, it is killed, but only when its exe is the installed copy. Copies running from anywhere else are never killed; you're asked to exit them from their tray menu.
  2. Copies itself to the install path, retrying for up to 30 s while the file is locked. It writes `.new` first, then replaces the old exe.
  3. Creates or overwrites the one Start Menu shortcut (`Steam LZX Tray.lnk`, on by default) and removes any stray `Steam LZX Tray*.lnk`. It never creates a desktop shortcut.
  4. If Start with Windows is on, rewrites the single Run value `SteamLZXTray` to `"<install path>" --tray`.
  5. Starts the installed copy, which shows an "Installed" or "Updated" balloon, then exits.
- Running the same downloaded exe again re-installs it in place. Launching the installed exe a second time just opens the running instance's window.
- **Uninstall** (Settings → Uninstall...): removes the Run value and the Start Menu shortcut, asks whether to keep your data, then exits. A hidden `cmd` waits for the process to end and deletes the install folder, and the data folder too if you chose not to keep it. Files that are already compressed stay compressed and work normally.
- Installer errors are shown in a message box and written to the log.
- **Automatic updates** (Settings → *Automatically install updates*, on by default):
  - About 2 minutes after startup and then every 6 hours, the app asks GitHub (`/repos/bstone108/SteamLZXTray/releases/latest`, unauthenticated) for the latest release. Drafts and prereleases are ignored.
  - If the release tag (4 numeric parts) is newer than the running version, it downloads `SteamLZXTray.exe` and `SHA256SUMS.txt` to `%LOCALAPPDATA%\SteamLZXTray\updates\<version>\` and verifies the SHA256. It then launches the download with `--update`, which runs the normal installer silently: state is saved, the exe is replaced and relaunched, and an "Updated" balloon appears.
  - If the hash doesn't match, the download is deleted and nothing is installed. If a silent update fails after the old copy stopped, the existing install is restarted.
  - *Check for updates now* is on the Settings tab. With automatic install off, it asks before installing.
  - The Status tab shows the current version and the result of the last check, and every step is logged.

## What it does
- **Finds libraries**: reads the Steam path from `HKCU\Software\Valve\Steam\SteamPath` (falls back to `HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`), then parses `steamapps\libraryfolders.vdf` (new and old formats). Libraries on volumes that aren't NTFS, are read-only or networked, or are on a drive you've added to **Excluded drives** (empty by default) are listed as *Skipped* and never touched.
- **Custom folders**: in Settings, use Add/Remove to include non-Steam folders. Each top-level subfolder is one unit, plus one unit for loose files in the root. The same checks, idle gating, skip list and resume apply.
- **Per-file walk**: games are walked one at a time, only those with `StateFlags == 4` (fully installed), in `steamapps\common\<installdir>`. For each file:
  - Already WOF-compressed (reparse tag `IO_REPARSE_TAG_WOF`, read straight from the directory listing): skipped instantly.
  - Smaller than the minimum size (default 4 KB), NTFS-compressed, encrypted, sparse, cloud placeholder, or another reparse point: skipped. Junctions and symlinks are never followed.
  - Otherwise the file is opened **exclusively** (share none) and `FSCTL_SET_EXTERNAL_BACKING` is called with WOF_PROVIDER_FILE / LZX.
  - **In use** (sharing or lock violation): skipped for now, tried once more at the end of that game and again on the next pass. It is never skip-listed.
  - **Not beneficial** (`ERROR_COMPRESSION_NOT_BENEFICIAL`, or no size reduction) or a hard failure: added to the **skip list** (path + size + mtime). The file isn't tried again until its size or mtime changes.
  - **Timestamps are preserved**: the original created/accessed/modified times are written back after compressing.
- **Idle gating**, checked before every file and samples every second:
  - CPU load from other processes, with this app's own CPU subtracted (default max 50%).
  - No game in the foreground: Steam's `RunningAppID != 0`, or the foreground process's exe is inside a Steam library or custom folder, or (optional, on by default) any fullscreen app. The lock screen, sign-in/UAC screens, screensavers and Windows shell windows never count; while the PC is locked or a screensaver is running, the CPU and drive limits are ignored and work runs (still at lowest priority).
  - Disk throughput from **other processes** on the drive being compressed (default max 50 MB/s, configurable). It's read from the PDH counter `\LogicalDisk(X:)\Disk Bytes/sec` (English counter names, so it works on any Windows language) minus this app's own I/O. Existing `DriveThresholdMBps` values in `settings.json` are kept.
  - After any wait, conditions must stay good for about 5 s before work resumes.
- **Lowest priority**: the worker thread uses `THREAD_MODE_BACKGROUND_BEGIN` (very low I/O, low memory priority) plus `THREAD_PRIORITY_IDLE` plus EcoQoS (thread power throttling). Process I/O priority is set to Very Low. The UI thread stays normal.
- **Passes survive restarts**: once a pass starts (scheduled, Steam-update-triggered, or Run Now), it stays in progress across exits, reboots and power loss. The pass type is saved too, including Run Now's "ignore the game log" override, along with the queued Steam updates and the pass-start snapshot of every Steam game's install state. On startup an unfinished pass resumes immediately, still idle-gated per file, and runs until it completes (the saved snapshot is reused, not retaken). If a resumed pass has no snapshot (for example it was started on an older build), that pass is fully reset once: the skip list and this-pass progress are cleared, a fresh snapshot is taken, and every game is walked again (the game log is kept but not used to skip games on that pass; all-time totals are kept). Already-compressed files are still skipped quickly at the file level. The new snapshot prevents this reset from running again. Only then does the app go idle. New passes come only from the schedule, a detected Steam update, or Run Now. A scheduled time missed while the app was off starts one pass on next launch, never more than one.
- **Resume after power loss**: the walk position (current unit plus last completed relative path) is saved at least every 2 s, and immediately after each in-use file. Saves are atomic: write a temp file, FlushFileBuffers, then ReplaceFile, which keeps a `.bak`. A truncated or corrupt `state.json` falls back to `.bak`.
- **Game log**: after a game is fully walked with no in-use leftovers, its appid + buildid are recorded. Scheduled runs skip games whose buildid hasn't changed. Triggered runs walk every queued game even if the buildid is unchanged, so a verify or repair that rewrote files without bumping the buildid is still recompressed. Custom subfolders are recorded with a signature (latest mtime + file count) and skipped while it's unchanged.
- **Triggers**:
  - Schedule: daily at HH:mm, or every N hours, or manual only.
  - **Watch for Steam updates** (checkbox): a FileSystemWatcher on each `steamapps\appmanifest_*.acf`, debounced 30 s (64 KB buffer). Each game's last known ACF state (buildid, LastUpdated, SizeOnDisk, StateFlags) is persisted so missed notifications can still be caught. At startup, if no pass is already in progress, every appmanifest is scanned against that last known state; any fully installed game (StateFlags 4) that changed or is new is queued. The same full scan runs if the watcher reports an error (for example a buffer overflow). The first scan with no baseline records current state without queueing every game. During a pass the live watchers are paused. At the start of every pass the app snapshots each game's ACF state and saves it with the resume state. When the pass finishes, and before live watching is turned back on, every ACF is scanned and compared to both the pass-start snapshot and last known state; any fully installed game that changed or is new is queued. The Status tab's existing Queued Steam updates row shows the count.
  - **Run Now**: walks everything and ignores the game log. The per-file check and skip list still apply.
  - **Pause/Resume** is available from the window and the tray menu.
- **Tray**: starts as a tray icon only. Left-click opens the Settings & Status window, and closing the window hides it back to the tray. The icon spins (12 frames, 80 ms) while compressing, shows an amber hourglass while waiting for idle, and is grey with pause bars when paused. The tooltip shows a short status. The right-click menu has Open / Run Now / Pause / Exit.
- **Single instance**: launching it again just opens the running instance's window.
- **Status tab**: short labelled rows (state, waiting reason, library, game, file, schedule, counts, savings, CPU and drive MB/s, version, live update-check progress). Long values and paths are shortened with an ellipsis; hover to see the full text. The library list is sized to its rows (1 to 5, then it scrolls) and the window height fits the tab.
- **Library list**: Type, Path, FS, Games / folders, and Compressed (completed unit count only). Path fills leftover width so the list never needs a horizontal scrollbar; long paths are cut with an ellipsis. Counts are loaded from `gamelog.json` at startup and update live as each game or folder completes.
- **Start with Windows** (checkbox, applies immediately): writes or removes the single value `HKCU\...\CurrentVersion\Run\SteamLZXTray = "<exe>" --tray`. It always points at the install path. The checkbox reflects the real registry state.
- **Start Menu shortcut** (checkbox, on by default, saved in settings): creates or overwrites exactly one `Start Menu\Programs\Steam LZX Tray.lnk` pointing at the install path, and deletes any stray `Steam LZX Tray*.lnk` there. It never creates a desktop shortcut. The setting is re-applied on every start and every install.

## Locations
- Program: `%LOCALAPPDATA%\Programs\SteamLZXTray\SteamLZXTray.exe`

## Data (all in `%LOCALAPPDATA%\SteamLZXTray\`, kept across updates)
| File | Purpose |
|---|---|
| `settings.json` (+`.bak`) | settings (including the Start Menu shortcut on/off) |
| `state.json` (+`.bak`) | resume position, run stats, queued update triggers, pass-start game snapshot, last-known ACF state, totals |
| `gamelog.json` (+`.bak`) | compressed games (library+appid → buildid) and custom-folder signatures |
| `skiplist` | append-only, fsync'd per line: `size|mtime|reason|path` |
| `logs\SteamLZXTray.log` | rotates at 2 MB, keeps 5 |
| `updates\<version>\` | verified auto-update downloads (old ones are cleaned up) |

## Caveats
- **Steam updates and verifies rewrite files uncompressed.** Turn on *Watch for Steam updates* (or rely on the schedule) to recompress them.
- **Anti-cheat**: some anti-cheat titles may object to WOF-compressed binaries. If a game misbehaves, verify it in Steam (which rewrites it uncompressed). There is no per-game exclusion list yet, so a later run would compress it again.
- **DirectStorage** games aren't detected or excluded in this build. Compression works, but decompression costs CPU when the game loads assets.
- **Kopia / SteamBackup.ps1**: compression never changes ACF files, so ACF-triggered backups aren't affected. WOF doesn't change file contents as read by applications, and this tool explicitly restores mtimes, so mtime-based change detection shouldn't re-read compressed files. The NTFS *change time* (ctime) does change; that only matters if your backup checks ctime.
- A very large file is compressed in a single kernel call, so idle checks happen between files, not mid-file.
- Drive-busy detection subtracts this app's own I/O, using the larger of the process I/O counters and the bytes it knows it read and wrote. That's an approximation: cached reads and WOF writes may be attributed differently.
- **Excluded drives** is empty by default, so every compatible drive is processed. Add drive letters in Settings to keep the tool off them.
- The installer compares paths with a case-insensitive full-path match. Launching through an 8.3 short path or a symlinked folder would be treated as "not installed" and simply re-install.
- Very old test builds (0.1.0-test) don't know the installer's exit signal. If one is still running, the installer asks you to exit it from its tray menu first.

## Versioning and releases
- Versions are `YYYY.MM.DD.BB`: the **America/Chicago** date plus the build number for that day, every part zero-padded (for example `2026.11.26.01`, then `.02`). A new number is used only when a release is cut.
- `VERSION.txt` holds the current release version. `scripts/next-version` prints the next one from the Chicago date and existing tags; `--write` also updates `VERSION.txt`.
- **Releases are built only from tags.** Pushing a tag `v2026.11.26.01` runs `.github/workflows/release.yml` on `windows-latest`. It publishes the self-contained single-file exe and creates the GitHub Release with `SteamLZXTray.exe`, `SHA256SUMS.txt` and `SteamLZXTray-<version>-win-x64.zip`. Pushes and PRs don't build; a manual *workflow_dispatch* run builds an artifact but publishes nothing.
- The app shows the full tag version (InformationalVersion, e.g. `2026.11.26.01`). The Win32 file version drops leading zeros (`2026.11.26.1`) because each part is limited to 65535.
- To cut a release:
  ```
  v=$(scripts/next-version --write)
  git commit -am "Release $v" && git push
  git tag "v$v" && git push origin "v$v"
  ```

## Building locally
Requires the .NET 8 SDK (it builds on Windows or Linux; `EnableWindowsTargeting` is set). The version comes from `VERSION.txt` unless you pass `-p:AppVersion=...`:

```
dotnet publish src/SteamLZXTray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Quick checks for state/settings file compatibility, the VDF parser and formatting (plain console app, no packages, runs on Linux too):

```
dotnet run --project tests/SteamLZXTray.Tests
```

## License
MIT. See [LICENSE](LICENSE).
