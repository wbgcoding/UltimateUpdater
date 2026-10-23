# Changelog

All notable changes to Ultimate Updater. Format: [Keep a Changelog](https://keepachangelog.com),
versioning: [SemVer](https://semver.org).

## [Unreleased]

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
