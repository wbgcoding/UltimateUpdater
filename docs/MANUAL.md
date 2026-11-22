# Manual

What the app shows and what each part does.

## The table

Every row is one available update, from one source, grouped by category (Software, System &
drivers, Games, Developer tools, Store) and sortable by any column. A row shows the installed
version and the available one where both are known; some sources only ever report the available
one. Steam's row expands into the individual game updates it found pending; every other row stands on
its own.

The filter chips above the table narrow the list, for example to critical updates only. Ignored
updates are hidden by default; the matching chip shows them again. Click the new version of a row
to read its release notes.

## First start

There is no setup window. The first start scans right away with every source the app finds on this PC; a
source whose tool is not installed simply shows no rows. Single sources can be switched off under
Settings, Sources, and the scan at every start can be turned off under Settings, Automatic scans.

## Rows you can install and rows you can open

- **Installable rows** have a checkbox: winget, Windows Update, Chocolatey, Scoop, pip, npm, .NET tools,
  the PowerShell Gallery, the NVIDIA driver and the other package managers. Tick them and press Update selected.
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

## Uninstalling

The Uninstall button in the toolbar (Ctrl+U) opens a window of its own that lists what is installed:
programs and Store apps, package manager entries, drivers and Windows updates. Every row shows its
publisher, version, size, install date, how it would be removed (silently, in the program's own window,
or not automatically) and a risk level. The last three sections start collapsed.

Tick the rows to remove and press Uninstall selected. Select all, Shift+click ranges and Space on several
highlighted rows never tick a row rated Risky; such a row is ticked only by its own checkbox. A selection
with Risky rows lists them with the reason and asks you to tick that you understand they can affect
Windows or other programs before the Uninstall button works. Rows that cannot be removed automatically
are greyed out without a checkbox. Stop lets the removals already running finish and starts no further one.

When the run ends, a summary line counts what was removed, what failed and how many leftovers were
removed. For every removed program the window then looks for what it left behind (its folders, registry
keys and shortcuts) and lists them under the program's name. Only an item the scan is sure belongs to
the removed program starts ticked. Remove leftovers takes the ticked ones: folders and shortcuts go to
the recycle bin, registry keys are exported to the registry backup folder first. Items in Program Files,
ProgramData or HKLM need administrator rights and are removed by the elevated helper. The window cannot
start a new run while the removals or the leftover step are still working, and closing it mid-run does
not stop them.

Before the removals start, the window can create a system restore point. Settings, Install, Backup before
uninstalling decides when: Automatic (the default) creates one when a program that needs administrator
rights is among the ticked rows or a row rated Risky is, Always creates one for every run, Off never does.
At most one restore point is made per 24 hours, shared with the install restore point: a run within that
time says which point it uses instead. If Windows cannot create one, the window asks whether to uninstall
anyway; Cancel leaves everything as it was. Every removed program is also written to the history.

Leftovers you remove after an uninstall are not deleted at once: folders and shortcuts are moved into a quarantine
(per user below the local app data folder, for all users below ProgramData, readable by administrators only) and
registry keys are exported there before they are removed. In the history, the entry of the removed leftovers
has a Restore leftovers button for 14 days: it moves the items back and imports the registry exports. A place
that exists again is left alone and counted, never overwritten; restoring all-users items asks for administrator
rights. Quarantine folders older than 14 days are deleted at the next removal. An item that cannot be moved
(another drive, a locked file) goes to the recycle bin as before and is not restorable from the history.

A row of the update table can be removed from its context menu too. Rows of sources that have an uninstall of their own (winget, Scoop, Chocolatey and similar) always offer it. A row found through its GitHub repository, or a game launcher itself, offers Uninstall only when it matches exactly one installed program by name (and publisher, where both are known). With two possible matches, or none, the entry is simply not there, so nothing is ever removed by a guess. Drivers, Windows updates and the games of a launcher never get it. The installed programs are read when you first open such a menu and kept until the next scan finishes. The removal asks first, makes the restore point the backup setting asks for, and lands in the history like a removal from the uninstall window.

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
| Drivers | Devices that are no longer connected but still carry a third-party driver, and third-party driver packages no device uses; never inbox drivers, every package exported to the data folder first (a copy also stays in `%ProgramData%\UltimateUpdater\driver-backups`), the backups of the last five runs are kept, all rows off by default; in the same section, older Python versions (off by default) | Yes |

## Appearance

Settings, Appearance sets the theme (follow Windows, light, dark, or one of two coloured dark themes) and
the language. Buttons, switches and highlights use the Windows default blue on every PC. The switch "Use
Windows accent colour" hands that choice to Windows instead, so the app follows the accent colour from the
Windows settings, live. The two coloured dark themes keep their own colour, and Windows high contrast
always uses the system colours.

## Where the data lives

Installed: `%APPDATA%\UltimateUpdater`. Portable: a `data` folder next to the executable. If that
folder cannot be written to (Program Files, a read-only share), the portable copy uses `%APPDATA%`
instead. Settings, install history, cleanup history and logs all live in that one folder, and
uninstalling the app leaves it behind. You can move it in Settings, and Reset everything in Settings
clears caches, history and logs.
