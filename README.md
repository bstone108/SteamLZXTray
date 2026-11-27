# Steam LZX Tray 0.1.0-test2 (test build)

A Windows 11 tray tool that compresses your Steam libraries (and any custom folders you add) in the background using **WOF LZX**, the same compression as `compact /c /exe:lzx`. It is not NTFS LZNT1.

Single self-contained exe (`SteamLZXTray.exe`, win-x64, .NET 8 runtime bundled). It is an unsigned test build, so SmartScreen may warn on first launch.

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
  - CPU load from other processes, with this app's own CPU subtracted (default max 30%).
  - No game in the foreground: Steam's `RunningAppID != 0`, or the foreground process's exe is inside a Steam library or custom folder, or (optional, on by default) any fullscreen app.
  - Disk throughput from other processes on the drive being compressed, via the PDH counter `\LogicalDisk(X:)\Disk Bytes/sec` minus this app's own I/O (default max 20 MB/s).
  - After any wait, conditions must stay good for about 5 s before work resumes.
- **Lowest priority**: the worker thread uses `THREAD_MODE_BACKGROUND_BEGIN` (very low I/O, low memory priority) plus `THREAD_PRIORITY_IDLE` plus EcoQoS (thread power throttling). Process I/O priority is set to Very Low. The UI thread stays normal.
- **Resume after power loss**: the walk position (current unit plus last completed relative path) is saved at least every 2 s, and immediately after each in-use file. Saves are atomic: write a temp file, FlushFileBuffers, then ReplaceFile, which keeps a `.bak`. A truncated or corrupt `state.json` falls back to `.bak`.
- **Game log**: after a game is fully walked with no in-use leftovers, its appid + buildid are recorded. Scheduled and triggered runs skip games whose buildid hasn't changed. Custom subfolders are recorded with a signature (latest mtime + file count) and skipped while it's unchanged.
- **Triggers**:
  - Schedule: daily at HH:mm, or every N hours, or manual only.
  - **Watch for Steam updates** (checkbox): a FileSystemWatcher on each `steamapps\appmanifest_*.acf`, debounced 30 s. When a game is back at StateFlags 4 with a new buildid, it's queued.
  - **Run Now**: walks everything and ignores the game log. The per-file check and skip list still apply.
  - **Pause/Resume** is available from the window and the tray menu.
- **Tray**: starts as a tray icon only. Left-click opens the Settings & Status window, and closing the window hides it back to the tray. The icon spins (12 frames, 80 ms) while compressing, shows an amber hourglass while waiting for idle, and is grey with pause bars when paused. The tooltip shows a short status. The right-click menu has Open / Run Now / Pause / Exit.
- **Single instance**: launching it again just opens the running instance's window.
- **Start with Windows** (checkbox, applies immediately): writes or removes the single value `HKCU\...\CurrentVersion\Run\SteamLZXTray = "<exe>" --tray`. It always points at the install path. The checkbox reflects the real registry state.
- **Start Menu shortcut** (checkbox, on by default, saved in settings): creates or overwrites exactly one `Start Menu\Programs\Steam LZX Tray.lnk` pointing at the install path, and deletes any stray `Steam LZX Tray*.lnk` there. It never creates a desktop shortcut. The setting is re-applied on every start and every install.

## Locations
- Program: `%LOCALAPPDATA%\Programs\SteamLZXTray\SteamLZXTray.exe`

## Data (all in `%LOCALAPPDATA%\SteamLZXTray\`, kept across updates)
| File | Purpose |
|---|---|
| `settings.json` (+`.bak`) | settings (including the Start Menu shortcut on/off) |
| `state.json` (+`.bak`) | resume position, run stats, queued update triggers, totals |
| `gamelog.json` (+`.bak`) | compressed games (library+appid → buildid) and custom-folder signatures |
| `skiplist` | append-only, fsync'd per line: `size|mtime|reason|path` |
| `logs\SteamLZXTray.log` | rotates at 2 MB, keeps 5 |

## Caveats
- **Steam updates and verifies rewrite files uncompressed.** Turn on *Watch for Steam updates* (or rely on the schedule) to recompress them.
- **Anti-cheat**: some anti-cheat titles may object to WOF-compressed binaries. If a game misbehaves, verify it in Steam (which rewrites it uncompressed). There is no per-game exclusion list in this test build yet, so a later run would compress it again.
- **DirectStorage** games aren't detected or excluded in this build. Compression works, but decompression costs CPU when the game loads assets.
- **Kopia / SteamBackup.ps1**: compression never changes ACF files, so ACF-triggered backups aren't affected. WOF doesn't change file contents as read by applications, and this tool explicitly restores mtimes, so mtime-based change detection shouldn't re-read compressed files. The NTFS *change time* (ctime) does change; that only matters if your backup checks ctime.
- A very large file is compressed in a single kernel call, so idle checks happen between files, not mid-file.
- Disk-busy detection subtracts this process's own I/O, using both the process I/O counters and the bytes it knows it read and wrote. Some WOF writes may still be attributed to the system, so treat the drive threshold as approximate.
- **Excluded drives** is empty by default, so every compatible drive is processed. Add drive letters in Settings to keep the tool off them.
- The installer compares paths with a case-insensitive full-path match. Launching through an 8.3 short path or a symlinked folder would be treated as "not installed" and simply re-install.
- If an older 0.1.0-test copy is still running, for example from the Desktop, it doesn't know the exit signal. The installer asks you to exit it from its tray menu first.

## Building
Requires the .NET 8 SDK (it builds on Windows or Linux; `EnableWindowsTargeting` is set):

```
cd src
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ../publish
```

## License
MIT. See [LICENSE](LICENSE).
