# Changelog

All notable changes to Ultimate Updater. Format: [Keep a Changelog](https://keepachangelog.com),
versioning: [SemVer](https://semver.org).

## [Unreleased]

### Updates
- Install progress is honest: a percentage shows only while a download reports one; the install phase
  shows a moving bar with "Installing…" and the elapsed time instead of a bar that stopped at 90 %.
  Download and install phases of winget, pip and Chocolatey are recognised in English and German.
- pip installs the exact offered version. When that version cannot install (a dependency missing for
  this Python version, or a conflict with other packages) the row says why and the version stays
  hidden until a newer one comes out, instead of ending as "Version unchanged".
- An uninstall through the same package manager waits while an interactive retry runs.
- Monthly Windows security updates no longer show an absurd size (such as 90 GB); an implausible size
  reported by Windows Update is left blank.

### Cleanup
- Unticking single files in an area's details no longer lowers the found total, only the selection.
- A finished cleanup shows one figure: the space freed.
- The freed total no longer counts bytes twice when the admin helper drops out, and the live figure no
  longer drops back after a failed area.
- Admin areas the background measurement could not reach are measured when the window opens instead of
  showing 0 B.
- Right-click a category or an area to rescan just that part.
- A category or an area's details show the amber or red dot when something ticked inside carries
  that risk, so it is visible while collapsed.

### Look
- Every progress bar is a clean rounded pill, also while it starts filling and at its end.
- Running installs line up their bars in one column and use the free width.
- The queue sits on the install strip's own surface; its versions line up with the arrow in the
  middle, like in the table, and the critical mark is larger.
- Scrollbar handles stay at least 48 points long, however long the list.
- The accent is the Windows default blue on every PC; Settings can switch to the Windows accent colour.

## [1.1.1] - 2026-10-25

### Updates
- Several installs run side by side more often: rows queued while a batch runs start at once, an
  uninstall can run beside an install batch, and only installs through the same package manager wait
  for each other. pip installs run in parallel per Python installation.
- Rows that need manual work show a retry button right in the row.
- Updating Git asks first while Git's shells, ssh or gpg are still running.
- Stopping winget also ends installers that broke away from it.
- Scan processes run at below-normal priority, so the PC stays responsive during a scan.
- Exit codes are read more accurately: Chocolatey's own codes, and pending-restart codes from pnputil and
  Python uninstalls count as success. A failed PowerShell module step names its reason.
- pip no longer retries with `--user` inside a virtual environment.

### Cleanup
- Measuring is much faster and can run in the background after the first scan (setting, on by default,
  skipped on battery); the window shows when the values were measured.
- The component store is analysed on its own whenever admin rights are available, with the same
  measuring dots as every other entry.
- The freed amount climbs live during a run and shows under the ring percentage; the total below now
  reads "x GB found".
- Windows Disk Cleanup leaves no profile behind in the registry after a run.
- Freed bytes stay correct when the elevated helper drops out, and a recycle bin call that timed out no
  longer moves its file later.

### Fixed
- Moving the data folder keeps the current history and reports, skips files deleted during the copy
  instead of failing, and removes the new folder again when a later step fails, so a retry works.
- The main window opens where it was last placed.
- A lone version is centred under the arrows above it.
- Queue rows keep the normal mouse cursor; the log settings no longer sit on a tinted surface.

## [1.1.0] - 2026-10-23

First public release.

### Updates
- One table for every update on the PC: winget, Chocolatey, Scoop, Windows Update (quality, driver and
  definition updates), NVIDIA GeForce drivers, pip, pipx, uv, pyenv-win, npm, pnpm, Volta, Bun, Deno,
  .NET tools, the PowerShell Gallery, RubyGems, Composer, rustup, vcpkg, mise, Flutter, the Android SDK,
  the Google Cloud SDK, GitHub CLI extensions, VS Code extensions, tools released on GitHub and Ollama
  models.
- Steam shows pending game updates; the Microsoft Store, Xbox and more than a dozen game launchers and
  developer hubs are detected and get an "Open" button.
- Grouped by category, searchable, sortable and filterable; ignored updates stay hidden until you ask
  for them. Release notes open from the new version of a row.
- Several installs at the same time (adjustable from 1 to 5). Admin installs in one batch share a single
  UAC prompt, handled by one elevated helper; the main app never runs elevated.
- Programs that are still open are listed before a batch starts: close them, skip their updates or try
  anyway.
- Optional restore point before the first admin batch, and a rollback to the previous version for winget
  packages.
- Scheduled scans that only notify, a tray icon and Windows notifications.

### Cleanup
- Temporary files, crash dumps, thumbnail and shader caches, package-manager and build-tool caches.
- Recycle bin with an age filter (only files deleted more than 30 days ago by default), and a switch that
  sends cleaned files to the recycle bin instead of deleting them.
- Windows Update and Delivery Optimization caches, setup and servicing logs, Windows Disk Cleanup and the
  component store cleanup with an analysis of what it would free.
- Drivers of devices that are no longer connected and unused driver packages, each exported to a backup
  first; leftovers of interrupted pip installs and older Python versions.
- Orphaned registry entries, always after a registry backup.
- Drive optimisation and system repair (`sfc`, DISM), both opt-in.
- Every entry shows its size and a risk level; only safe entries start ticked. Paths can be excluded for
  good from the right-click menu.

### General
- Light and dark theme following Windows, and Windows high contrast.
- Eleven languages: English, German, Spanish, Italian, Portuguese, Polish, Simplified Chinese, Japanese,
  Dutch, Turkish and Romanian.
- Portable executable (x64) and an installer for x64 and ARM64 with all users, current user and portable
  modes.
- Self-update from this repository's releases, applied only when the download matches `SHA256SUMS.txt`.
- No telemetry and no account. The log redacts user paths and anything shaped like a credential.
