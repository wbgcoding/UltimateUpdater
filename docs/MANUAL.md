# Manual

What the app shows and what each part does.

## The table

Every row is one available update, from one source, grouped by category (Software, System and
Drivers, Games, Developer Tools, Store) and sortable by any column. A row shows the installed
version and the available one where both are known; some sources only ever report the available
one. Rows can carry children of their own: Steam's row expands into the individual game updates
it found pending. Every other source's rows stand on their own.

The Filter button above the table opens a menu that narrows the list, for example to critical updates
only. Ignored updates are hidden by default; the menu can show them again. Click the available version of
a row to read its release notes.

## First start

There is no setup window. The first start scans right away with every source the app finds on this PC; a
source whose tool is not installed simply shows no rows. Single sources can be switched off under
Settings, Sources, and the scan at every start can be turned off under Settings, Automatic scans.

## Rows you can install and rows you can open

- **Installable rows** have a checkbox: winget, Windows Update, Chocolatey, Scoop, pip, npm, .NET tools,
  the PowerShell Gallery, the NVIDIA driver and the other package managers. Tick them and press Install.
- **Open rows** have no checkbox: the Microsoft Store, Xbox, Steam and the other game launchers. The app
  can see that an update is waiting but cannot install it, and it does not invent a version number it was
  never given. The only action is "Open", which takes you to the launcher's own update page.

## Open in the launcher

"Open in ..." starts the launcher or store page and stops there. The app cannot see what happens next, so
the row is never marked installed or failed; it stays until the next scan no longer finds the update. A
package manager that can only open its own confirmation window for a package is handled the same way.

## Installing

Ticked rows go into one queue. Updates that run as your user start right away. Updates that need
administrator rights are collected and handed to one elevated helper, so a single UAC prompt covers the
whole batch. Before the first such batch you can create a restore point. Each row shows its own progress,
and one failed update never stops the rest.

If a program that is about to be updated is still open, a short window lists it before the batch starts
and asks once for all of them: close the programs and continue, skip those updates, or try anyway.

## Cleanup categories

The cleanup window groups its targets into seven sections. None of them touches your own folders
(Documents, Downloads, Pictures and so on); only temporary files, caches, the recycle bin and Windows'
own maintenance tools, plus the opt-in registry, driver and old Python version removal, are in scope,
and nothing runs until you press the button and confirm.

| Section | What it touches | Needs administrator rights |
| --- | --- | --- |
| Temporary files | The current user's own temp folder and crash dump folder always; every other user's temp folder, the Windows temp folder and memory dump files additionally | Partly: only the all-users, Windows-owned and memory-dump entries |
| Caches | Thumbnail cache, GPU shader caches, package-manager caches, build-tool caches, and other apps' own cache folders | No |
| Windows maintenance | Windows Update's download cache, Delivery Optimization's cache, Windows' own servicing and setup logs, Disk Cleanup's categories, the component store cleanup (DISM, with an optional ResetBase that is off by default), and drive optimisation (TRIM for SSDs, defragmentation for hard drives), one row per fixed or USB drive whose type is known | Yes |
| Recycle bin | The recycle bin's contents, filtered by age (only files deleted more than 30 days ago by default; adjustable). A switch in Settings sends cleaned files to the recycle bin instead of deleting them for good | No |
| System repair | `sfc /scannow` and a DISM health scan, run as separate selectable steps, both off by default | Yes |
| Registry | Orphaned uninstall entries and other safe-to-remove registry leftovers, only ever after a registry backup is written | Yes |
| Drivers | Devices that are no longer connected but still carry a third-party driver, and third-party driver packages no device uses; never inbox drivers, every package exported to the data folder first (a copy also stays in `%ProgramData%\UltimateUpdater\driver-backups`), all rows off by default; in the same section, older Python versions (off by default) | Yes |

## Where the data lives

Installed: `%APPDATA%\UltimateUpdater`. Portable: a `data` folder next to the executable. If that
folder cannot be written to (Program Files, a read-only share), the portable copy uses `%APPDATA%`
instead. Settings, install history, cleanup history and logs all live in that one folder, and
uninstalling the app leaves it behind. You can move it in Settings.
