# Changelog

Format: [Keep a Changelog](https://keepachangelog.com), versioning: [SemVer](https://semver.org).

## [Unreleased]

## [1.1.0] - 2026-10-23

### Security (2026-10-23)
- A data folder pointer is only followed into a folder named after the app, so Reset everything can never empty logs, cache or reports folders of an unrelated folder.
- Before a recycled folder is deleted, every link inside it is removed on its own first, so nothing the delete meets can lead outside that folder.

### Fixed (2026-10-23)
- A hung recycle bin operation no longer blocks a cleanup for ever: it gives up after five minutes, and Stop ends it at once.
- An oversized or damaged cleanup history file no longer freezes or breaks the cleanup window.

### Changed (2026-10-23)
- Reset everything also removes damaged copies of settings and history, the crash note and the autostart entry.
- The README lists every host the app talks to and what each one receives.
- The app updates itself from public releases only.

### Security (2026-10-17)
- For an all users install the app starts the verified setup with administrator rights itself, so Setup never has to start its file again while Windows asks for rights.
- The administrator helper refuses to work when profiler, garbage collector or startup hook variables are set, and the app removes them before it starts the helper.
- Tools started by the app also lose user preload settings when the app itself runs as administrator.

### Fixed (2026-10-17)
- Stop on an update waiting for its second attempt now cancels it instead of letting it install.
- Cleanup exclusions of Shared DLLs and startup entries saved before the last change keep protecting their values.
- Composer prerelease versions update with the plain update again instead of failing.
- Updates refused while the app applies its own update show a notice instead of nothing.
- The Turkish reboot badge fits its column again.
- The driver and old Python cleanup rows no longer say "nothing found" beside a list of findings.
- pip packages show up again when a package description contains an emoji: every pip call now runs with UTF-8 output.
- The settings section list follows the page to its last section when scrolled to the bottom.
- The registry cleaner no longer offers working 32 bit registrations for deletion.
- Permanent cleanup exclusions also protect folders beyond the visible list and rows not scanned yet.
- A changed recycle bin age filter can no longer swap the list after the cleanup was confirmed.
- A null table layout in settings.json no longer stops the app from starting.
- The three strikes protection survives a restart.
- Destructive confirmations no longer react to Enter, and Ctrl+Enter installs the whole selection again.
- After an update of an all users install the app starts without administrator rights.
- Stop during the retry round or while rows wait for a slot really stops them.
- Quitting from the tray while installs or a cleanup run asks first; Windows shutdown no longer waits on that question.
- Chocolatey, Scoop, npm, pipx, uv and VS Code no longer offer phantom updates or downgrades.
- Driver cleanup keeps its result and its backup when stopped midway.
- User names in logs are hidden in more spellings (Python paths, short names, other profiles).

### Changed (2026-10-17)
- The driver cleanup row looks and works like every other row that needs administrator rights: tickable, the run asks for the rights, a declined prompt skips the drivers.
- The cleanup button's disk no longer grows and shrinks while a cleanup runs and sits a little smaller inside the button.
- Registry entries the cleanup cannot delete are logged as one line per category instead of one line each.
- npm, pnpm and Volta read package data from the registry as it arrives and keep only the few fields they use, so large packages no longer cost hundreds of megabytes of memory per scan.

### Security (2026-10-16)
- Self update downloads work again: GitHub now serves release files from a new host that the allow list refused.
- The administrator helper and the tools it starts no longer take over startup hooks, profilers or Node, Python and Java preload settings from the user's environment.
- The verified setup file of an installed self update stays locked while Windows asks for administrator rights.
- With the app started as administrator, a per-user Python uninstaller is never run; it is reported as skipped.
- Package ids and versions with command shell characters are refused before they reach a batch file tool.
- A confirmation whose window fails to open falls back to a box with OK and Cancel instead of confirming by itself.
- Logs also hide bare GitHub, npm and GitLab tokens and Bearer values.
- Dependabot also watches the NuGet packages.

### Changed (2026-10-16)
- The System language follows every language the app ships, not only German and English.
- npm, pnpm and Composer global packages update to the new major version the table offers.
- Source names such as "VS Code extensions" are translated.
- Long cleanup area names are shortened with "…" and show in full on hover.
- A Windows light or dark switch while the app runs is followed at once with the "System" theme.
- A self update that finishes downloading while updates, a cleanup or a drive optimisation run waits for them and then installs by itself.
- The finished cleanup dialog keeps its Rescan button.
- The cleanup history button is named "Cleanup history", apart from the main history.
- The size column is a little wider so its sort arrow always fits.
- Pressing F5 or "Check now" in the tray no longer restarts a scan that is already running.

### Fixed (2026-10-16)
- Windows high contrast colours are kept instead of being replaced by the dark palette.
- Microsoft Store updates from winget appear in the table; ids winget shortened with "…" are left out.
- Driver and older Python rows get their entries when the app itself runs as administrator.
- Driver cleanup without administrator rights skips its rows instead of breaking later driver backups.
- The cleanup confirmation lists selected driver and Python removals.
- A Temp folder reached under two spellings is measured once, so an excluded entry is never cleaned under the other one.
- Unticking one registry entry no longer excludes the other entries under the same key.
- A registry backup that fails half way no longer leaves truncated files behind.
- A damaged cleanup history file is kept aside instead of being overwritten.
- The cleanup group header can no longer show more selected areas than it has.
- A failed source keeps its rows from the previous scan.
- A package waiting for its automatic second attempt no longer shows as failed and cannot be installed twice.
- A self update waiting for running work no longer closes the app while a new batch starts.
- The source chips no longer rebuild on every progress tick, so their buttons and menus respond.
- Stop also ends a single source rescan.
- A scheduled scan skipped during an install runs once the install is done instead of a day later.
- The disk space and metered connection warnings count download sizes found after the scan.
- GitHub release and Ollama checks show an error when no lookup succeeded instead of "up to date".
- Registry lookups can no longer hang on a stalled download; large npm package listings are read in full.
- Stuck rows explain themselves in the chosen language.
- Dialogs and buttons are released when they close instead of staying in memory.
- Endless animations pause while the window is hidden or minimized, and the cleanup animation respects the Windows animation setting.
- Dialogs fit the screen of their owner window at mixed display scaling.
- Translations: restored accents in seven languages, dates read as dates, and several German, Italian, Turkish, Japanese, Dutch, Polish and Romanian texts were corrected.
- The app cache sweep no longer empties installer caches (Package Cache), Store app data (LocalCache) or the browser history database (WebCache).
- Disk Cleanup can no longer run handlers outside its allow list that another tool had marked.
- "Move to the recycle bin" never deletes an item permanently; an item that cannot be recycled stays where it is.
- Emptying the recycle bin removes only the items shown in the scan, not ones deleted afterwards.
- A Disk Cleanup run that hits its time limit (now 30 minutes) says so instead of reading as stopped.
- The skipped count after a cleanup counts files only.
- A forged recycle bin entry can no longer end the recycle bin scan.
- Administrator cleanup targets ticked after the scan now ask for elevation instead of running without it.
- The headline no longer says "up to date" when every installed source failed.
- The scan progress bar reaches its end.
- The "no connection" notice clears when a new scan starts.
- A source stopped on its own reads as cancelled and keeps its rows.
- A package that only installs per user is no longer shown as failed before its retry, and is tried at most twice.
- Declining a downgrade from the history leaves no stray row.
- A Windows Update install waiting on a system repair no longer holds up a file cleanup.
- "Close and continue" treats a program it cannot inspect as still running.
- The last log lines of the administrator helper are kept.
- The portable self update always leaves the new copy running and removes the old file.
- Installed self updates no longer leave their setup file in the temp folder.
- The status column fits in Polish, Portuguese, Turkish, Spanish and Dutch.
- Startup no longer reads the details cache on the window thread, and the cache is only rewritten when it changed.
- On a metered connection the skipped interval scan is logged once, not every minute.

### Changed (2026-10-10)
- Unsorted column headers no longer show a sort mark; the sorted column keeps its arrow.
- The dark theme uses neutral grey surfaces and no Mica backdrop.
- The colour legend under the cleanup details is gone.
- With administrator rights already granted, the component store is measured after the cleanup scan without a click.
- Android SDK packages update one at a time through the Android command line tool.

### Fixed (2026-10-10)
- Android SDK updates no longer fail on every row when a beta system image would be downgraded.
- A locked Android package (running emulator) gets a clear explanation.
- The recycle bin age box shows the typed number again.
- Closing the Cleanup window keeps the main window in front.
- A failed log archive no longer leaves an empty zip file behind.
- winget's "no uninstall command found" error gets a clear explanation.

### Changed (2026-10-04)
- The scan button is now called "Check for updates".
- The filter switches are folded into one Filter menu; ignored updates are hidden by default and come back with a "Show ignored" switch.
- The Cleanup button is labelled, and the columns button sits in the table header.
- The per-row update button is neutral instead of accent coloured.
- Every sortable column header shows a sort mark and a tooltip.
- The publisher column starts hidden, and columns that no row has a value for are hidden automatically.
- The group chevron sits beside the category name.
- The headline counts queued and running installs, and the subheadline no longer shows the critical count.
- The title bar shows the app name alone; the portable note moved to the About card.
- The available version link opens the changelog.
- Failed source chips show the word "Error", and the quiet sources line in the footer is gone.
- While installing, one global stop button replaces the stop button on every row; the per-row stop glyph is filled in the critical colour.
- The window before a batch has a clearer title and skip button, shows why a blocked update was left out of the batch, and shows the restore point line only for batches that need admin rights.
- A failed row shows the short failure reason under its name.
- Successful history entries say what they did and are painted in green.
- Failed packages are always retried once; the setting is gone.
- Settings: the scan timing options are merged into one card, a section list sits beside the page, the metered connection and download check texts are reworded, and the admin hint moved into the tooltip of the card capsule.
- The recycle bin option and the component store analysis moved into the Settings cleanup card; "areas" is now the one term for cleanup categories in all languages.
- The Cleanup window is organised under two captions, shows the free space gained instead of the total afterwards, says "found" under the ring, and lets the restore point line wrap.
- The system repair rows in Cleanup start switched off, and the default selection names the component store cleanup.
  Existing installs get both rows unticked once; ticking them again afterwards is kept.
- The first start no longer shows a welcome window; the first scan begins at once.
- The manual group is renamed and its icon matches.

### Removed (2026-10-04)
- The Close button and the Select all button in the Cleanup window, the Select all button in the main footer, the group by type switch and the risk level filter in Cleanup.

### Fixed (2026-10-04)
- Admin tools are only started from folders that only administrators can change, including the rights of parent folders and the target of shortcuts to tools.
- Admin tools started by the app get a trusted program search path and working directory.
- Driver backups are created in folders protected for administrators only.
- Moving the data folder no longer follows linked folders into other locations.
- A damaged saved scan result no longer causes errors.
- A retried install starts with fresh progress and transfer figures, and late progress from an earlier attempt is ignored.
- A Cleanup scan that was replaced by a newer one no longer overwrites the current results, and starting a cleanup waits correctly for a running scan.
- Only the exact Python version is matched when uninstalling a Python version.
- Two installs at the same time can no longer both create a restore point.
- Update information is read more carefully, and very large or invalid disk sizes no longer break the totals.
- Settings are no longer lost when the app closes while a save is pending.
- The admin helper keeps running cleanly if a listener fails.

### Changed (2026-10-03)
- Before a batch starts, the window now asks only once for all blocked updates: Close program and continue, Skip or Try anyway start the batch directly.

### Fixed (2026-10-03)
- "Try anyway" in the window before the batch did nothing visible and needed a second click on Start.
- Tooltips in Settings and Cleanup were white in the dark theme.
- If a blocking program is not closed, the update no longer starts anyway.
- Cancelled updates can be started again, count as done in the progress and show up in the history as cancelled instead of failed.
- An error when starting the admin helper during the component store analysis can no longer close the app.
- The component store analysis no longer runs twice when Cleanup is reopened.
- Rescanning a single source no longer ends the display of a running full scan.
- A cancelled scheduled scan no longer reports updates from incomplete results.
- Reset also deletes the last saved scan result.
- The theme chosen in the welcome window no longer stays active when the window is closed without Next.

### Added (2026-09-26 to 29)
- Driver cleanup: disconnected devices with a third-party driver and unused driver packages, each row with
  version, date, size and time of the last connection; the tooltip names the vendor, path, file and, when
  there is one, the currently installed version for comparison.
- New cleanup targets: leftovers of pip, older Python versions (off by default), component store cleanup and
  ResetBase (off by default), plus an analysis of the component store.
- pip checks every installed Python version and leaves out updates that would break other packages.
- A "Default selection" button in the Cleanup window.
- Short vendor name in the table, also for pip packages; the tooltip shows the full name.
- Readable names for AI models and an explanation for the GameInput error.

### Changed (2026-09-26 to 29)
- New app icon with a download arrow.
- Much smoother with large lists: filters, "Select all", running installs and the history no longer
  recalculate per row; cache and checks run off the interface thread.
- The Cleanup window opens at once and scans in parallel; animations pause while a window is hidden.
- The rescan button on each source is gone.
- The window also starts fully visible on small screens.

### Fixed (2026-09-26 to 29)
- Driver names no longer appear in a foreign script, and headings and the size column of the driver lists
  are readable and complete.
- The columns menu closes reliably on the second click on "Columns".
- After a language change, category headings, the update notice and the server list follow at once.
- The install bar stays visible when several installs run at the same time; a disabled main button shows
  readable text.
- No more crash when a drive is unreadable while Cleanup opens.
- Fixed Windows Update sizes, "Needs admin rights" while the admin helper is running and registry entries
  that could not be deleted.
- The log no longer loses its last lines on exit or on a crash.

## [0.3.0] - 2026-09-25

### Added (2026-09-12/13)
- Several installs at the same time: the upper limit is adjustable (1 to 5), winget may run in parallel up
  to that limit, within one source installs still go one after the other, different sources run side by
  side; installs without and with admin rights now run at the same time instead of one after the other.
- A confirmation window before every install batch: it warns about too little disk space, a low battery and
  a metered connection, names blocking running programs, shows the latest restore point and combines several
  questions into one instead of asking them one by one.
- Single queue entries can be stopped without interrupting the rest of the batch.
- An automatic second attempt at the end of a batch for failures where that is worthwhile (interrupted
  download, blocked file, concurrent install); critical packages are installed first within a batch.
- Downgrade action: an install can be set back to the version it was last updated from.
- The history now shows the size of each install in its own column, and colored safety dots in Cleanup now
  also mark single files, not only whole categories.

### Changed (2026-09-12/13)
- Restore point at most once every 24 hours instead of for every batch, which saves up to a minute of
  waiting before the first package.
- Registry backups no longer land in the profile root folder by default but in a folder of their own under
  the program data.
- Queue without position numbers: a permanent drag handle replaces the number, and entries can now also be
  reordered with Alt and the arrow keys.
- Several windows (history, Cleanup, content dialogs, list windows) now adapt their size to their actual
  content instead of always taking the same fixed space; the Cleanup window also starts a little taller.

### Fixed (2026-09-12/13)
- The queue scroll bar no longer overlaps rows, the main window now also fits smaller screens (1366 by 768),
  and the scroll bar handle stays grabbable on long lists.
- The Cleanup detail list now opens much faster instead of visibly stalling with many entries.
- The version arrows in the history were inconsistent when the version strings had different lengths; sorting
  now follows the target version.
- Log lines of the elevated install helper now appear promptly instead of arriving in a delayed bunch.
- A cancelled source no longer stays on "waiting" for good, and a late cleanup progress message no longer
  overwrites a result that is already finished.

### Added (2026-09-17/18)
- Search now also finds vendor and source, both also selectable as new table columns that are hidden by
  default; columns and sorting are now saved.
- Rescan a single source without starting a full scan.
- Unknown error codes can be looked up offline (Windows' own text for the code).
- Release notes are visible even before the self-update, now also for npm packages.
- Recurring failures get their own filter, the history can be copied to the clipboard, and an error report
  can be created with one click; the log folder and crash reports can now be reached directly from Settings.
- Recycle bin cleanup can be filtered by age, with a reminder when it has been a long time since the last
  cleanup.
- A list of the servers the program contacts in the About section of Settings, and a dialog of its own for
  managing the ignore list.
- The data folder can be moved in Settings, and sources that are not installed can be hidden.
- Keyboard shortcuts for the most important actions, with an overview on F1; portable mode is now shown in
  the title bar.

### Changed (2026-09-17/18)
- Downloads are no longer scanned for viruses by default (signature and checksum checks always run anyway);
  when the scan is switched on, it now runs faster.
- A source now counts as hung when it has been silent for a while, no longer after a fixed run time, so that
  a large but slow scan is not cancelled by mistake; the program also explicitly follows the proxy entered
  in Windows and tries once directly if needed.
- Registry cleanup merged into two understandable categories; the backup stays mandatory and is now
  explained right at the table.
- Duplicate ways to do the same thing removed (such as a doubled "Try again" button); "Reset everything" is
  now clearly recognizable as a dangerous action.
- A thorough accessibility pass: every control now has a name for screen readers, a sensible tab order, and
  focus stays inside open dialogs instead of escaping from them.

### Changed (2026-09-25)
- The program is now called Ultimate Updater throughout instead of Windows Updater, including the data
  folder, program identifiers and all visible texts; a one-time migration takes over existing settings and
  the history automatically.
- New app icon in a dark glass style with a blue ring, and the name together with the running version now
  sit next to the logo in the title bar.
- The dark theme now feels closer to the Windows default instead of too dark, and the settings page now has
  two visibly different surface levels instead of a single one.
- The "Source" column is dropped in favor of a "Vendor" column that is visible from the start (the source
  stays visible in the tooltip and in the error report); version and arrow now show one shared tooltip
  instead of two separate ones.
- The search button spins its icon while a scan is running; the queue header is larger and the entries
  themselves are set closer together so that more of them are visible.
- The column chooser button in the history window is gone (it stays in the main window); the green word
  "Done" next to the green check mark in Cleanup is gone as well, the check mark alone is enough.
- The risk level "Delicate" is now called "Risky" and is shown in red instead of yellow; all secondary
  windows now carry the app icon.
- New cleanup icon: a hard disk with a platter, ring and flashing sparks instead of the previous shape.
- "About" and the server list in Settings start collapsed; six fixed dropdown widths became minimum widths
  so that long words are not cut off; the text about the download scan now names exactly the three affected
  files.
- Cleanup only blocks installs where both really touch the same files (file deletion, disk cleanup); system
  repair, registry cleanup and defragmentation now run alongside instead of waiting, except system repair,
  which never runs at the same time as a Windows update.
- Drives of undetermined type (such as external USB disks) now also appear in Cleanup, with an honest label
  instead of silently missing.

### Added (2026-09-25)
- Five more languages: Chinese, Japanese, Dutch, Turkish, Romanian; Spanish, Italian, Portuguese and Polish
  were completed and can now be selected.
- A banner now shows when the results still come from the last start and are being checked again; the same
  banner also reports when there really is no connection to the network, so that blocked sources do not look
  like broken ones.

### Fixed (2026-09-25)
- A package that had already been installed successfully was sometimes offered again as available; the
  program now checks after every install whether the version really changed.
- The download progress bar could look finished before the row really was; a second install attempt after a
  failure showed no progress any more, and a cancelled download wrongly counted as completely downloaded.
- The "Update blocked" window cut off its buttons and left empty space below them; the release notes window
  left an empty block behind when the text was short.
- Ctrl+A no longer selected any rows after a click into the table.
- Registry, app cache and install leftovers cleanup had stopped running altogether since the split into
  parallel lanes and were silently reported as "skipped".
- Several packages of one batch that require a restart now lead to one shared prompt instead of one prompt
  per package.
- The Windows component store analysis and missing drive rows in Cleanup (for SSD optimization, for example)
  showed no or wrong results; both now show real numbers and all detected drives respectively.
- The Android SDK source failed on a new output format of the tool and is now parsed correctly again; an
  ordinary npm error code was wrongly reported as a crash instead of a normal failure.
- A failing source needlessly lengthened the whole scan and filled the log with repeated error messages;
  both are now limited.
- "Reset everything" now really resets every setting.
- Several race conditions between Cleanup and scan fixed: a cleanup started during a scan now waits for it
  to end, registry cleanup no longer blocks installs, "Done" only appears once the restore point has really
  been created, and the scan lock is released in every case.

### Removed (2026-09-25)
- The number in the taskbar icon (a badge with the count of available updates) was removed, it was too
  small to read; the notification for each scan stays.

### Security (2026-09-25)
- The elevated install helper no longer follows a data folder pointer that the user can write to, and
  therefore no longer creates folders or files as administrator in arbitrary places.
- Error texts are now shortened and sanitized before they are saved in the settings file, so that a single
  large error can no longer silently reset all settings to default.
- The registry backup was hardened several times: it now runs entirely in memory instead of through a
  swappable temporary file, handles unusual value types and sizes correctly, no longer follows registry
  links into parent keys, and an incomplete or unverified backup never counts as a success any more; a wrong
  system clock can no longer prevent restore points until a date in the future either.
- The age filter in the recycle bin no longer empties the whole recycle bin when every displayed row happens
  to be ticked.
- Old registry backups are only deleted through a verified handle.
- A rollback version that looks like a command option is now rejected.
- Moving the data folder now always ends up in a separate `UltimateUpdater` subfolder, refuses the current
  directory as well as temp and cache locations as a target, and fails instead of silently leaving a
  still locked file behind.

### Added (2026-09-06)
- About card in Settings: product name, version, MIT license notice, link to the GitHub repo, third-party and
  trademark notice.
- Offers to install winget itself when a scan reports it as missing: once per session, after consent,
  signature and publisher checked, without admin rights.
- MIT license. `LICENSE` in the repo root, a license page in the installer (MIT text plus the previous
  disclaimer), and the README got privacy and trademark sections.

### Fixed (2026-09-06)
- Several short freezes removed: settings are now written decoupled (before, up to 750 ms on the UI thread,
  now 0 ms noticeable), search caches matches per row (5.5 ms per keystroke over 500 rows), the scan table
  tick backs off to 500 ms from 100 rows, the cleanup history fills in one go instead of entry by entry
  (3000 entries: 1.1 ms instead of thousands of single messages).
- The message dialog had a fixed minimum height, which made short messages show needless empty space.

### Removed (2026-09-05)
- The AMD, Intel, Dell and Lenovo driver sources, they only detected the hardware and opened the vendor
  page without ever resolving a real version or installing anything. NVIDIA and Windows Update stay
  unchanged, both resolve a real version and install it.

### Fixed (2026-09-05)
- Windows Update could report "is no longer offered" although the update was still available. The install
  compared identifier and revision number together, but Windows Update keeps changing the revision, even
  between search and install. The comparison now uses the identifier only, and the currently offered
  revision is installed. If the identifier is really missing, the message stays, and the app then
  automatically rescans only Windows Update so that the row disappears by itself.
- **The "Close program or skip" prompt appeared twice when two updates were blocked by the same running
  program.** Every blocked row asked on its own although it was the same decision. Blocked rows are now
  grouped by the blocking program first, then each group is asked only once and the answer is applied to all
  affected rows. With more than one affected update, the dialog text now also names their number.

### Changed (2026-09-02/03)
- All user-facing date and number displays (due dates, percentages, byte sizes, cleanup history) now follow
  the chosen display language instead of a fixed format.
- Several texts that wrongly showed the plural for exactly one item ("1 entries" in the plural form, "1 areas
  cleaned" and others) now correctly show the singular.
- Numerous wording and expression errors cleaned up in both language files (German: "Manuell updaten" to
  "aktualisieren", "Anbieter" to "Hersteller", "Log" to "Protokoll", "Verzeichnis" to "Ordner",
  "Administratorrechte" to "Admin-Rechte" and others; English: "administrator rights" to "admin rights",
  two sentence fragments completed).
- Several files with multiple types split into one file per class; 7 duplicate helper functions merged,
  5 dead code spots removed, 3 timeout constants centralized.
- `System.Management` and the test tools (`coverlet.collector`, `Microsoft.NET.Test.Sdk`,
  `xunit.runner.visualstudio`) raised to the current versions.

### Fixed (2026-09-02/03)
- A redirect could silently send a request started over HTTPS to plain HTTP before the check could even
  take effect. Every redirect step is now checked individually before it is sent, instead of only
  afterwards.
- A hanging WSUS operation silently blocked a cancel for up to 15 seconds; two cleanup steps needlessly
  blocked a thread pool thread in the process.
- A single faulty icon could silently abort the loading of all other icons of a scan.
- The Cleanup dialog showed a group header as "partially selected" although in fact nothing was ticked.
- The main window always opened 60px larger than the stored size said, because that size was below the
  actual minimum size.
- A date in the cleanup history could show a wrong separator depending on the system setting, regardless of
  the chosen display language.

### Security (2026-09-02/03)
- A shared network connection now refuses a redirect that changes from HTTPS to plain HTTP.

### Added (2026-08-29/30)
- "Remind me later": a row can now be postponed for a chosen duration instead of only being ignored. It
  disappears from the default view, a badge of its own shows the due date, and a filter of its own shows
  postponed rows.
- Packages that keep failing are marked as "Manual work" instead of repeating the same error endlessly: if
  an install fails three times in a row in the same classified way, the row now says so honestly, with a
  "Try normally again" button.
- Uninstall in the context menu now works for nine more sources: RubyGems, Composer, vcpkg, mise, the
  Android SDK, gcloud components, Ollama, Volta and pyenv-win.

### Fixed (2026-08-29/30)
- An empty state message showed the wrong text when a filter (for example "Hide ignored") really hid ALL
  found updates: it wrongly said "no updates found" instead of "nothing matches the current filter".
- A tool name field deliberately inserted into the output of `volta list` could have been read as a command
  line flag instead of a package name during install or uninstall. Closed.
- The progress display of an install run never counted a row marked "Manual work" as done, so it stayed
  too low at "X of Y" for good.
- An interactive second attempt never counted toward the three failed attempts that mark a row as "Manual
  work". So it could fail at the same problem any number of times without ever being marked.
- The selection summary (count and size of the selected rows) stayed at the old state after a search or
  filter change until some other event happened to refresh it.
- An uninstall that hit an unexpected error left the affected row stuck in the "running" state for ever
  instead of showing an error.

### Security (2026-08-29/30)
- A new standing test prevents settings or history entries from getting a field that looks like a secret or
  like personal data.
- Proven instead of merely claimed: traffic logging really writes only the host name of an outgoing
  connection, never a path, query parameter or token.

### Removed (2026-08-22)
- The export button (raw CSV/JSON export of the scan results), which nobody asked for, took up space in
  the toolbar that the buttons actually in use now have.
- The `dotnet workload` source: on some SDKs it showed update suggestions for packages that did not exist.

### Changed (2026-08-22/23)
- The toolbar buttons are now right-aligned, and the search bar and the filter toggles wrap cleanly onto a
  second line in a narrow window.
- The sources that have to be opened by hand (Steam, Store, driver pages) now scroll together with the
  table instead of in a separate area of their own.
- The version column now keeps arrow and numbers exactly aligned, regardless of whether a row shows an
  installed version or a changelog button.
- Error states of a row now show a clear word ("Error"/"Handed over") instead of an ellipsis, with a
  working info button for details in both cases.
- The Cleanup dialog now feels calmer: the former sweeping animation with sparks and a rotating ring has
  given way to a soft, gliding surface of light.
- The detail list in the Cleanup dialog now sits tighter together (the mark directly next to the checkbox
  instead of offset beside it).
- GitHub updates that have to be downloaded by hand now have a category of their own.
- The changelog and error details dialogs now show a visible scroll bar for long text.
- The "Open log folder" button in Settings now sits next to the path instead of below it.

### Fixed (2026-08-22/23)
- **A second click on another row during an interactive second attempt or an uninstall could start a
  second, colliding install run**. It is now queued instead.
- Memory use in long sessions: the cache for enrichment data (release date, size) kept growing without
  limit. It now has the same hard upper limit as the icon cache. Two small resource leaks in the tray icon
  (font, menu outline) fixed.
- Column headers and the source marker could only be operated with the mouse. They can now also be reached
  by keyboard (Enter/Space).
- The Cleanup dialog cut off its button row at its own stated minimum width. Fixed.
- Three smaller inconsistencies in the German texts (form of address, quotation marks, abbreviation).
- Security and privacy pass repeated: no new findings.
- Findings from a final second review: a held Enter/Space key on a column header that had just been made
  operable fired the command several times per second instead of once; a narrow special case could wrongly
  report a job to the elevated process as failed on a connection restart although it was running; a second,
  related special case could disturb a running interactive second attempt when another row was uninstalled
  in the meantime; the history dialog wrote log lines through an instance of its own that was not
  coordinated with the rest of the app.

### Fixed (2026-08-16)
- A hardened self-update path. The updater already refused to talk to anything but GitHub, but it
  only checked that before a request. A redirect could still have carried both the download and its
  checksum somewhere else, and the checksum would then have vouched for the wrong file. It now re-checks
  where a response actually came from, after every redirect, and refuses anything off the list before a
  single byte reaches the disk.
- Missing release dates and download sizes. Updates that a source reported in the same instant it
  finished scanning were skipped when the extra details were fetched, so they stayed blank for the rest
  of the scan. They are now filled in like every other row.
- Wrong "found" and "failed" counts after a restarted scan. Starting a new scan while the previous
  one was still finishing let the old scan add its numbers to the new one's totals.
- A clipped button in the German dialog that asks whether to close a program blocking an install:
  the three buttons needed more room than the fixed dialog width allowed, so the leftmost one was cut
  off. The dialog now grows to fit its buttons and stays exactly as wide as before everywhere else.
- Memory use during long sessions: the icon cache kept every icon it ever resolved, for as long as
  the app ran. It is now capped.

### Added (2026-08-15)
- Explanations for failed installs, not just an error code. A failed update (Visual Studio's own
  setup, an MSI, a driver, Windows Update itself) now shows a plain-language line above the raw output:
  what happened and what to try next. Dozens of known codes are covered, including the case that started
  this: winget's raw "Exitcode: 8006" now reads as Visual Studio's setup being blocked by running
  programs, in plain words.
- The app offers to close what's blocking an install. Before installing something like Visual Studio,
  if the program itself (or a short list of known troublemakers) is still running, a dialog offers to
  close it and continue, skip it, or try anyway. Closing asks nicely first and only force-closes after a
  second, separate confirmation, never silently and never while unattended.
- A one-time interactive retry for the rare install failure only a visible installer window can fix:
  offered only for that kind of failure, exactly one attempt, only on your own click.
- Windows Update failures explain themselves too. The bare "result code 5" now becomes a real
  sentence ("Windows Update is already busy", "a restart is pending"), and the app now waits for Windows
  Update to be free and checks for a pending restart before it even tries.
- Settings is reorganized into fewer, clearer cards. Closely related switches (Install, Cleanup and
  registry backup, logs and history, Appearance) now share one card with hairline-separated rows instead
  of a card per switch. The cleanup card also gained a manager for paths you've permanently excluded from
  cleaning, with a release button per path and a "release all" option; a factory reset now actually
  clears that list too.
- Every recycled file is now its own row, grouped by drive, instead of one lump sum per drive: pick
  and choose exactly which deleted files to remove for good.
- Live status while cleaning: each item shows Waiting / Running / Done / Stopped / Skipped, and an
  open detail list marks individual files as they're cleaned, in real time.
- The cleanup animation is livelier: a circling shimmer, a pulsing dot riding the progress arc, a
  broom that reacts to how fast work is actually happening, and a small celebratory pulse with a
  counting-up total when a run finishes.
- A changelog link with no text now tries to fetch it first. Clicking the info button on a GitHub or
  GitLab release now tries to pull the real release notes before giving up; only when that fails do you
  see the small "open the vendor's site" prompt instead of an empty window.
- Cleanup cards and detail lists look calmer and behave more precisely: a real card look instead of a
  plain arrow, checkboxes that stay lined up with the title no matter how much grows underneath, and long
  file lists get their own small scrollbar instead of growing the whole dialog.

### Fixed / Changed (2026-08-15)
- The toolbar and the install/queue banner got a layout pass. The four icon buttons (Cleanup, Export,
  History, Settings) now sit to the left of search instead of docked on the right, and the running-install
  banner gets its own full-width row so search and every button stay usable throughout a batch install.
  It also looks tidier: rounded corners, a quiet accent edge, a fill that glides instead of jumping, a
  total-size figure beside the queue, and simpler queue-row cards.
- A long version number can no longer push the changelog button off the edge of the window at the
  app's minimum size.
- The app is published as "BGCoding" in the exe and installer properties, matching the rest of the
  branding.
- A round of correctness and safety fixes, worst first. An elevated install now verifies the exact
  file it is about to run rather than a file it checked a moment earlier, closing a narrow window where
  the two could differ. A hand-edited settings file with certain fields cleared to nothing no longer
  crashes the app. Cleaning more than one registry-related item in the same run can no longer delete one
  item's data before its own backup was made, and anything you unticked or permanently excluded is now
  actually left alone by that cleanup instead of being removed anyway.
- Several places that could quietly lose data now don't: a factory reset clears every setting,
  including cleanup choices and exclusions; an accidental "0 days" retention no longer wipes your whole
  install history; history and cleanup records now save crash-safely instead of in a way that could leave
  them empty; "delete my data" now really removes every file it claims to, including older rotated
  history; pausing a cleanup run can no longer let one more item slip through and finish first; a failed
  automatic update now keeps its safety lock instead of quietly dropping it.
- A batch of smaller correctness fixes: opening a folder from the cleanup dialog no longer freezes it;
  a cancelled or externally-killed install is no longer misreported as a crash; a German-language
  "1,23 MB" was misread as a hundred times too large; a manipulated Steam file path can no longer point
  outside the game library; a registry backup could lose data if a deleted value happened to contain
  certain text, fixed; a Windows Update scan could fail outright on some systems; an old NVIDIA
  certificate check has been tightened; a broken entry in the update feed no longer hides every newer
  release after it; switching language while the cleanup dialog was open could leave its groups stale
  until reopened, fixed; a few sorting and date-parsing edge cases were corrected.
- Under the hood: added a standing test that scans the whole source tree for anything that looks like
  a leaked API key, token or password (nothing found); tightened a few more narrow security gaps (closing
  a blocking program is re-checked at the moment it actually happens, not only earlier; logged paths are
  scrubbed regardless of which way the slashes point; every network response the app reads is now
  size-capped); and a broad internal cleanup of code comments and duplicate logic with no user-visible
  effect.

### Added (2026-08-09)
- The cleanup dialog got a real overhaul. Each item now shows exactly what it's made of: files and
  folders, grouped and individually selectable, capped at 500 with "and N more" for anything larger, and
  a risk level (safe / considered / risky) with a one-line explanation on hover. A new "move to Recycle
  Bin instead of deleting" option is remembered like every other choice; anything needing administrator
  rights always deletes for good, regardless of that setting.
- Cleanup remembers what you last had ticked, instead of resetting every item to its default each
  time the dialog opens.
- The scan header now shows every source while a scan runs, not only the noteworthy ones, each with
  its own progress state and its own cancel button; cancelling one source leaves the rest of the scan
  untouched.
- The cleanup overview shows the facts that were missing: free disk space before and projected after
  your current selection, a top-3 list of what takes the most room, and when the drive was last cleaned
  and how much that freed.
- Cleanup history can be deleted, one entry or the whole log, and a failed entry now has its own
  "Details" button with the same error view a failed row in the main table already uses. Both the install
  history and the cleanup history now prune themselves automatically after a configurable number of days,
  the same as the log files.
- The cleaning animation's broom and sparkle are proper vector art instead of a blurry, stretched
  symbol-font glyph.
- Registry cleanup no longer scans the same category twice per run, making it noticeably quicker.

### Added (2026-08-10)
- Nearly every remaining source now offers a changelog or a link to one. Chocolatey, PowerShell
  Gallery, NuGet/.NET tools and Windows Update show real release notes; RubyGems, Composer, Scoop,
  rustup, Flutter, the Android tools, gcloud, the driver pages and Steam link to their own release page
  (Steam through its real news feed). winget's own release notes now feed the same button.
- Stores, launchers, Steam and driver rows now collect into one compact block at the bottom of the
  table instead of mixing into the main list, since none of them carry a real version to sort or filter
  by.
- Per-drive recycle bin scanning and emptying, a right-click menu on cleanup entries (open folder, copy
  path, permanently exclude), a "group by type" toggle for the detail list, and every remaining control
  reachable by keyboard.

### Fixed / Changed (2026-08-10)
- Hiding a risk level in the cleanup dialog no longer cleans it anyway. Unchecking "Risky" only hid
  those items from view. They stayed selected and were still cleaned (and counted in the confirmation
  total) without ever being shown again. Hiding now genuinely pauses a selection instead of acting on
  what you can no longer see.
- A few more edge cases from a follow-up review: the same "hidden but still acted on" issue in the check
  for whether an install needs administrator rights, a crash classification that could blame an installer
  for being cancelled or killed from outside, and a changelog link that skipped the usual http(s)-only
  safety check for two newly-added sources.

### Added (2026-08-08)
- The app keeps a readable record of what it did. Every scan, every update and removal with its
  versions and how long it took, every download with its size and speed, every cleanup run with what it
  freed, and every error: one short line each, in a file per day. Settings now shows where those files
  are, opens the folder with a button, and lets you choose how long they are kept: 3, 7, 30 (the default)
  or 365 days. Lines are deliberately terse and a message that repeats is counted rather than written
  again, so a tool stuck in a retry loop costs one line instead of a thousand. Self-update now checks in
  at each step too: found, downloaded, verified, applied (or exactly which of those failed), instead of
  the prose it used to leave behind.

### Fixed / Changed (2026-08-08, later)
- Buttons no longer turn black while you hold them down. The label and icon of a pressed button fell
  back to plain black in both themes; they now keep their proper colour, and a rendering test presses
  every control in both themes and fails if anything near-black appears that was not there at rest.
- A package that cannot be updated is no longer reported as a failed install. When winget knows a
  newer version but has none that fits this installation, the row now says so in a caution colour rather
  than turning red. Updates that need administrator rights and hand over to another program's own
  window are finally reported that way too, instead of as failures.
- Cleaning respects what you deselected. Two package-manager cache folders live inside the temporary
  files folder, so unticking the cache did not actually protect them, and ticking both counted the same
  megabytes twice. Every section now owns its own folders exactly once.
- The cleanup dialog reports the result where the work was chosen: each section shows what it freed,
  and the drive's free space before and after the run is shown under the total.
- Autostart is registered for the right account. With a separate administrator account on the
  machine, Setup wrote the autostart entry into that account instead of yours.
- Steadier scans: lookups for installed packages are no longer fired all at once at a registry, which
  is what got a well-equipped machine rate-limited into a scan full of holes. The update check gives up
  after a while instead of hanging silently, and the app closes a couple of seconds faster.
- Smaller fixes: the first-run window no longer pushes its buttons off the bottom when many package
  managers are missing, and a long update-banner title now shortens instead of pushing the version out
  of view.

### Fixed / Changed (2026-08-08)
- The install queue shows what it is actually doing. Each row now carries its position, a brand
  icon, the version it is moving to, the release date, a small mark for a critical update, and a badge
  when it needs administrator rights, instead of a bare name and a drag handle.
- More sources now show a release date and size: Volta, .NET global tools, npm/pnpm (when the
  registry omits its own size figure), Flutter, rustup and pyenv-win. winget's own rows fill in
  progressively in the background after a scan finishes, the same way every other source already does.
- winget's last-resort install attempt no longer skips the installer's hash check. It now looks up
  the exact version winget itself offers and installs that one directly, still fully verified, instead
  of forcing past every check. When nothing winget offers actually fits the installation, the message
  now says so plainly and names the version it tried, instead of a bare error code.
- The error-details window only highlights what is actually a marker: a rung label, the source's
  name, or an exit code, instead of bolding any word that happened to sit before a colon. Exit codes
  now read as a small marked badge, and every number field lines up in a column.
- The cleanup dialog is grouped into collapsible sections (temporary files, caches, Windows
  maintenance, the recycle bin, system repair, the registry) instead of one long list, each with its own
  total and a three-way "select all" for that section. Earlier cleanups moved into their own window,
  opened from a button, instead of sitting inside the list of what can be cleaned right now.
- Several cleanup-run correctness fixes: the progress ring no longer jumps backward when a target
  spans more than one folder; a stopped Disk Cleanup run no longer reports itself as finished; a restore
  point is now also offered before a run that touches the registry or repairs Windows' own files, not
  only before installs; the confirmation before cleaning lists the recycle bin and registry first, since
  those are the hardest to undo.
- The source list in Settings is easier to read: each source is now a single toggle chip instead of
  a checkbox squeezed inside one.
- Continued hardening across scanning, installing and cleanup: several edge cases around symlinked
  folders, malformed tool output, and background lookups are now handled more defensively. Nothing here
  changes what the app can do, only how carefully it does it.

### Fixed / Changed (2026-08-07)
- winget could go missing entirely. External tools were only ever looked for on `PATH`, and winget
  lives solely in the app-execution-alias folder. On a machine whose `PATH` has lost that entry, the
  source found nothing and reported it as "checked, nothing to update". That folder is now searched as
  a fallback, after `PATH`. What an elevated install is allowed to launch has not changed.
- The PowerShell Gallery source no longer needs NuGet's package provider. `Find-Module` pulls that
  provider in, and when it cannot be installed the error takes the whole scan down with it. Installed
  modules are still listed through PowerShell; the latest version now comes from the gallery's own web
  feed instead.
- Controls no longer turn black when pressed. Only buttons had been corrected before; the switches,
  checkboxes and toggles kept the theme's assumption that an accent is a light colour, which painted the
  knob of a switched-on toggle black. Everything sitting on the accent is now drawn in the same colour,
  in every state.
- A more compact table. Rows are shorter and sit closer together, categories included, so more fits
  on screen. A long name ends in an ellipsis rather than wrapping; the full name is on the tooltip.
- Registry cleanup now looks at per-user entries too. Application paths and COM registrations were
  read only from the machine-wide part of the registry, so anything registered for the current user
  alone could never be found. Both are backed up before any change, exactly as before.
- The violet theme is now called **Neon**; an existing choice carries over.
- Setup opens in front again. It could start hidden behind whatever window launched it, which looks
  as if nothing happened at all.
- A clearer disclaimer. It now also covers the cleanup: what it deletes, that deleted data is gone
  for good, and where responsibility lies.
- Uninstalling is more careful. It no longer removes a `data` folder it never created, and it only
  closes the copy in the installation folder; a portable copy running elsewhere is left alone.
- A per-user installation no longer offers a folder it cannot write to when a machine-wide
  installation also exists.
- The history window shows the whole history again. Once the log had grown past its rotation size,
  it showed only the newest entry, although nothing had been lost.
- A self-update that cannot start the new copy puts the old one back, instead of leaving the app
  unable to start. The downloaded file is also cleaned up afterwards rather than staying in the
  temporary folder.
- A single corrupt file can no longer take a whole source down: a Steam manifest with a nonsense
  date, an unexpected entry in the NuGet feed, or a scan status handler that fails.
- Composer release dates and sizes are matched exactly. Version 1.2 could pick up the details of
  1.20.

### Added
- Update sources: winget, Windows Update, Chocolatey, Scoop, pip, npm, .NET global tools and the
  PowerShell Gallery list and install; Steam reports pending updates and hands installing back to
  Steam; Microsoft Store/Xbox, Epic, GOG, Battle.net, EA, Ubisoft, Rockstar and Riot are detected and
  offer an "open" action instead of invented version numbers.
- One live table for all sources, grouped into collapsible categories, with fuzzy search, a
  critical-only filter, per-row ignore, and CSV/JSON export of the current view.
- Install queue that batches every update needing admin rights into a single UAC prompt, optionally
  creates a restore point first, streams per-row progress, and records every attempt in the history.
- Interval scan that only notifies, a tray icon (open / scan now / exit), close-to-tray and autostart.
- Self-update from GitHub Releases, SHA256-verified and fail-closed, offered as a banner with a
  one-click install.
- Portable single-file executable and an Inno Setup installer with a per-machine/per-user choice,
  both produced by `build.bat` together with `SHA256SUMS.txt`.
- German and English throughout, switchable without a restart; light/dark follows the system.
- Design specification (`docs/DESIGN.md`).

### Added (after the first review round)
- A **Sources** group in the settings: one switch per source. A source switched off is skipped
  entirely: no probe, no pill, no results, and a source the settings file has never heard of counts
  as on, so a source added by a later version does not silently stay dark.
- Bundled brand marks for rows whose own icon cannot be found on disk. Package-manager rows are
  the reason: an npm or pip package has no installed application to take an icon from. The marks come
  from the CC0-licensed Simple Icons set, are matched from an explicit table rather than by
  similarity, and never win over an icon extracted from the installed program
  (see `NOTICE-brand-icons.md`).

### Added (2026-07-23 feature round)
- Live install progress. External tools are now streamed as they run, so a running install shows a
  real percentage and, where the tool reports bytes, live throughput and megabytes transferred, not a
  jump from 0 to 100. The progress window also shows batch-wide downloaded/second and free disk space.
- NVIDIA / AMD / Intel driver rows. The GPU is detected from WMI; for NVIDIA the latest Game Ready
  driver is looked up and, when newer than what is installed, offered as a real download-and-install
  (verified NVIDIA-signed before it runs, one UAC prompt from the vendor installer). AMD and Intel,
  which expose no stable feed, link to their official download / auto-detect pages.
- Right-click Start and Uninstall. Every row's context menu can remove the item where the source
  supports it (winget, Chocolatey, Scoop, pip, npm, .NET tools, PowerShell Gallery, Steam) and start
  it where a launch target is known (Steam games, launcher clients). Removal is confirmed first.
- More launchers, split apart: the "open it yourself" section is now two groups, *Open in a store*
  and *Open in a launcher*, and HoYoPlay, Wargaming Game Center and Garena were added.
- Steam games are their own collapsible group. Triggering a Steam update from outside the client
  proved unreliable, so games are information only: real version data, but no checkbox, with an
  "Open" button to Steam's own downloads view instead.
- Download size and date columns, filled where the source knows them up front (Windows Update, the
  NVIDIA driver).
- Source pills toggle. Clicking a pill switches that source off (its rows leave the table at once)
  and back on; the setting is the same one the settings window shows.
- A per-row error Details view. A failed update carries the real tool output and code behind an
  info button, which is the one thing that says *why* an update would not apply.
- Settings redesigned as Windows-11-style cards (icon, title, description, control) that right-size
  the window, and a manual "check for app updates" alongside the now-periodic background check.
- Installer Portable mode: the installer can lay the app down portable (no autostart, no marker,
  data beside the exe) as well as installed.

### Changed
- The interval scan now also checks for a new version of the app itself, so a long-running copy keeps
  current rather than only learning about it on the next launch.
- winget's reboot-required exit codes (1641/3010) and "no applicable upgrade" now count as success.
  treating them as failures was most of why winget updates appeared to "mostly fail".
- Parallel installs by source were already in place (npm and pip update at the same time); the admin
  batch still runs sequentially behind its single UAC prompt.
- `build.bat`: `dist\` now holds only the **x64 portable exe and the installer** (plus `SHA256SUMS`).
  The portable build is x64 only; the installer still carries x64 and ARM64 and picks by OS. The
  installer sources both architectures from `build\publish\` so they never clutter `dist\`.
- The install-progress popup's scrollbar no longer floats over the last column, the source pills no
  longer touch when they wrap, and category groups collapse more evenly.

### Added / Fixed (same-day follow-up)
- The portable download is now `Windows Updater Portable.exe`: fixed name, no version or
  architecture, and the installer's Portable mode lets you choose exactly where it goes, via an
  early setup-type page instead of a late checkbox.
- Reverted: Steam auto-install (`steam://install`) was shipped this same day and then confirmed
  unreliable. Steam rows are information only again (real version data, no checkbox), as originally
  specced as the fallback if triggering an update from outside the client didn't hold up.
- The header now names which sources are still being checked while a scan runs, not just a count.
- Collapsing a category group now survives the table refreshing (new rows streaming in no longer
  silently reopens it).
- Settings: rounder combo boxes, a hover tooltip on every source explaining what it is and how it
  works, and the scrollbar now sits flush against the window's right edge instead of inset by 20px.
- Mock install progress (used for offline UI testing) now streams realistic incremental progress
  instead of jumping straight to 100%, and the install popup's own refresh timer is twice as fast.

### Fixed / Improved (2026-07-24 follow-up)
- pip permission errors recover. A pip update that failed because a machine-wide Python keeps its
  packages under Program Files is now retried as a per-user install (pip's own advice in that case),
  so it succeeds without administrator rights instead of just reporting "access denied".
- A failed update opens its details in a proper window: the item, its source and the version it was
  moving to, then the tool's own raw output in a copyable, scrollable panel, in place of a bare message
  box.
- Epic Games fixed. The row now shows the launcher's real icon, and its **Open** button reliably
  starts the launcher: it resolves the installed executable rather than a deep link that did not always
  open. Every launcher row (GOG, Battle.net, EA and the rest) now takes its icon from the client itself.
- Source pills are grouped by category, mirroring the table: Software, Store, System & drivers,
  Developer tools, Games, then Steam and the "open it yourself" launcher rows.
- Scrollbars sit flush against the window's right edge in the update list, the history window and the
  install-progress window, rather than inset from it.
- The installer's Portable mode always shows a folder picker. The folder page was being skipped once
  the app had been installed once, so choosing a location appeared to do nothing; it now defaults to a
  clean user folder, and the installer's wording was rewritten to be clearer and more professional.

### Changed / Fixed (2026-07-24, second round)
- Steam is a launcher, not a category. The separate Steam section is gone: Steam now sits with the
  other clients under *Open in a launcher*, and the games it has updates for hang under it as a
  collapsible sub-list. Each game keeps its real build ids and its own right-click Start / Uninstall.
- The store and launcher pills are one button each. Instead of a pill per launcher, the bar at the
  bottom shows a single *Open in a store* and *Open in a launcher* button that reports the combined
  count and switches all of its sources on or off together.
- The error-details window no longer opens with a sentence explaining itself; it shows the failure and
  the tool's output directly.
- pip: a locked file is no longer retried as a per-user install. "Access denied" also appears when a
  file is merely in use by another process, and the install location is perfectly writable. Retrying
  with `--user` there would quietly plant a second, shadowing copy instead of updating the one in use.
  Only a genuinely unwritable location is retried now.

### Changed / Fixed (2026-07-24, third round)
- The install progress window is gone. A running batch now shows in place of the search bar at the
  top: the current package, a live overall bar, the count and throughput, and pause/stop, while the
  table updates live underneath. A running row shows a bar with the percentage *beside* it, not written
  into it.
- More sources: RubyGems (`gem`), Composer (global) and Rust toolchains (`rustup`) are detected and
  updated where they are installed.
- Steam's game sub-list is larger and easier to read, and each game shows the date it was last
  updated where Steam records it.
- The new version's **date is shown to the left of the version**, consistently across the whole table
  and the Steam sub-list. It appears wherever the source reports a date (Windows Update, the NVIDIA
  driver, Steam games); the package managers do not expose a release date in their listings, so those
  rows show none.
- Scrollbars no longer sit over the category collapse chevrons; the chevrons keep clear of the bar.
- Settings dropdowns (language, theme, scan interval) paint each value on its own themed background, so
  the options are readable and selectable instead of vanishing into the popup surface; the Sources
  card's collapse chevron was pulled in from the edge.
- The installer is compressed harder (solid LZMA2 at the top preset), keeping both architectures and the
  self-contained runtime, with no features dropped for size.

### Changed / Fixed (2026-07-24, fourth round)
- Dates now fill in for far more updates, and progressively. After a scan finds every update, a
  background pass keeps working and enriches each row with a release date and download size read from
  the package's own registry (PyPI for pip, the npm registry for npm), facts the outdated listings
  never carry. All updates appear first; the extra detail streams in afterwards, so nothing waits on it.
  It fails closed: a slow or missing registry never disturbs the scan.
- Dates render as DD.MM.YYYY and sit in their own fixed column, so every row's date lines up as a
  clean lane, as do the version and size columns.
- The install top bar shows more. Alongside the current package it now shows that item's exact
  percentage and, where the tool reports bytes, its own download figure, plus the overall percentage
  beside the bar. When a tool reports nothing measurable, the bar advances on its own (download →
  install) instead of sitting frozen at 0.
- Store and launcher "open" rows are no longer selectable: those groups drop their select-all
  checkbox, since there is nothing to install there, and **double-clicking such a row opens it**
  (the store or launcher), matching what its button does.
- Failed installs always carry a useful log. The diagnostic now keeps the *tail* of the tool's
  output, where the real error is, instead of the opening banner, drops the progress-bar noise, and
  always ends with the exit code, so a failed row's Details view and the log are never empty or useless.
- The source pills sit in one flat wrapping row with no category headers, each sized to its content.
- The Steam sub-list header is larger, and the settings dropdowns' open list is now one solid
  themed surface instead of light-on-light.
- The tray (taskbar) right-click menu is themed to match the app (dark or light), with proper
  spacing, instead of the classic grey.
- The installer's mode page is centred. The Standard/Portable choice and its descriptions now sit
  in a centred column rather than hugging the left edge, the descriptions align tidily under their
  option, and the wording was tightened in both languages.
- .NET global-tool version checks run in parallel rather than one NuGet round-trip after another.
- Per-row Install button. Every installable row now carries an **Install** button that queues just
  that update; pressing it while a batch is running adds it to the same running queue. Manual rows keep
  their **Open** button. Table columns are reordered to date · size · version · action and every column
  lines up across rows.
- Rows no longer vanish after collapsing/expanding a category: the grouped list renders in full
  instead of relying on WPF's buggy grouped virtualization.
- The table has a symmetric right margin again, so the window has the same gutter on both sides.
- The **Store / Launcher / Steam** section headers are the same height and size as the other category
  headers, with their labels in the same column.
- The installer's Standard option is labelled **"Standard installation (recommended)"** (the word moved
  into the title), and the portable download is simply **`Windows Updater.exe`**, with no "Portable" in the
  name.
- Ignoring an update no longer hides it. An ignored update stays in the table, clearly marked and
  dimmed, and is not counted or installable; a **"Hide ignored"** toggle removes them for a clean
  overview, and each ignored row has an inline **"Show again"**. A row's right-click menu (updates only,
  stores and launchers have none) offers Start, Retry (on failure), Uninstall (with confirmation),
  **Ignore this update / Show again**, and **Ignore all updates**, themed to match the app.
- Failed installs can be retried with one click, without re-selecting everything.
- The history is searchable and can be filtered to failures only.
- The settings dropdowns are fully re-templated so both the closed control and the open list are
  solidly themed (rounded card fill, chevron, one dark popup surface), with no more flat grey.
- The table's scrollbar has its own gutter instead of floating over the rightmost buttons/icons.
- A pre-install size preview: the selection summary shows the combined download size of what is
  ticked (as sizes become known), so you see roughly how much will come down before starting.
- "Reboot needed" is now surfaced. An update that installs but needs a restart (winget's 3010/1641)
  is marked on its row and summarised in a dismissible notice after the batch. It is information, not a
  failure; the update did apply.
- Piecemeal per-row installs cost a single UAC prompt. Admin updates (winget, Chocolatey, Windows
  Update) queued one at a time are collected and installed together in one elevated batch.
- A "project page" link (changelog/homepage) appears in a row's context menu where the package's
  registry provides one, read from the data already fetched, http(s) only.
- Enrichment results are cached (release date, size, link) for a week, so a rescan does not query
  PyPI/npm again for a version it already knows.
- More sources get dates: Chocolatey (date and size from the community feed) and .NET global tools
  (date from NuGet) now fill in like pip and npm do. (winget has no public per-version date API, so its
  rows still show none.)
- Sortable columns. The table has clickable Name / Date / Size / Version headers: click to sort
  within each category group, click again to flip the direction (dates and sizes default to
  newest/largest first).
- Accessibility: source pills and the sort headers carry screen-reader names.

### Added (2026-07-25 round)

- Free up space. A new button left of the search box opens a cleanup dialog: your temp folder, every
  user's temp folder, the Windows temp folder, the Windows Update and Delivery Optimization caches, the
  thumbnail/icon cache, crash dumps, the recycle bin (off by default) and Windows' own Disk Cleanup. It
  measures everything in the background as it opens, a ring shows what the current selection would free,
  and the cleanup itself only ever starts after a second confirmation that lists what goes and warns,
  in red, that it cannot be undone. There is no automatic cleanup, by design. Files another process has
  open are skipped and counted rather than aborting the run.
- Six more update sources: Android SDK packages (`sdkmanager`), GitHub CLI extensions, pipx tools,
  VS Code extensions, .NET SDK workloads, and WSL (detected for the source picker; it is never shown as
  an update row because `wsl` cannot check for one without applying it).
- A welcome dialog on first start lists the update sources actually found on this PC and lets you
  choose which to use. Everything found is on by default. It returns after a factory reset.
- Two new themes: "Hacker" (neon green) and "AI enthusiast" (violet), both on the Fluent dark base.
- Reset and delete data at the bottom of the settings: back to factory defaults, optionally wiping
  every file the app wrote (settings, ignore list, history, logs, caches).
- History: every column now sorts (click to sort, click again to flip), plus filter buttons for a
  time range (today / 7 days / 30 days / all) and for each source that appears in the list.
- The install strip shows the batch's total download size next to the free-space figure.

### Fixed (2026-07-25, pre-release audit)

- The window no longer freezes while Windows asks for admin rights. Starting the elevated helper blocked
  the interface until the UAC prompt was answered. If the prompt ended up behind another window, the app
  looked hung.
- A confirmation no longer closes the window that asked it. Pressing "Clean up" made the cleanup window
  disappear and then delete everything with no progress and no cancel; "Reset everything" did the same in
  the settings.
- Switching back to light no longer leaves dark panels behind. Only the text used to change, which made
  light mode unreadable after using a dark or coloured theme.
- Startup no longer waits on the update-token lookup, so the window appears immediately.
- One error can no longer become a stack of error windows: the fatal-error report now reports once.
- An install whose tool left a background process behind no longer blocks the queue for half an hour and
  then claims success; a failed install's log can no longer come out garbled.
- Windows Disk Cleanup is skipped with an explanation when it cannot run unattended, instead of opening its
  own dialogs behind the cleanup window; closing the dialog during the repair step no longer interrupts DISM
  mid-write.
- An unreadable settings file no longer stops the app from starting, and one unreadable Steam library no
  longer hides every Steam game.
- The portable build can find its own update again (its file name and the updater had drifted apart).

### Fixed (2026-07-26, code review)

- A slow download inside one package no longer abandons the rest of the batch: only your own Stop does.
- An unknown source is now recorded in the history like any other failure instead of vanishing from it.
- If the elevated helper ever has to be restarted, the old one is released properly and can no longer
  interfere with the new session; a failing progress handler can no longer stall every other job.
- Turning the administrator prompt down no longer locks the cleanup out for the rest of the session.
  opening it again asks once more.
- Removed four unused texts and localised the selected-size suffix that was assembled in code.

### Fixed (2026-07-26, fifth review round)

- Stopping an install no longer leaves the queue stuck. Anything still waiting is put back to "not
  installed" instead of showing a progress bar for ever, and a second attempt starts normally. The
  connection to the elevated helper stayed out of step after a stop, so the next job waited for an answer
  that had already gone past.
- The elevated helper now works on several things at once. Measuring what a cleanup would free runs
  alongside an install; anything that actually changes the machine still runs strictly one at a time.
- Switching theme now repaints everything at once; the tray menu and a few status colours kept the
  previous theme until something else happened to refresh them.
- The Steam sub-list no longer shows an update glyph next to its heading; the row keeps its height.
- Settings dropdowns: the opening button sits flush with the window's right edge.

### Changed / Fixed (2026-07-26, fourth review round)

- Administrator rights are asked for once per session, not once per action. The first job that needs
  them starts a helper that stays for as long as the app runs; every later install, removal and cleanup
  uses it without another prompt. Closing the app ends the helper.
- Removing a package installed just for you now works. Windows refuses that when the request comes from
  an administrator, so it is retried without administrator rights instead of failing with a message you
  could do nothing about.
- Stopping a scan no longer reports a source as failed: stopping kills the tools mid-run, and that is
  not an error. The Search button is also usable again immediately instead of staying greyed out.
- The welcome dialog appears only on a genuine first run, and its text is shorter.
- Cleanup: DISM now runs before `sfc` (it repairs the store `sfc` restores from), the window can be dragged
  by its header, the ring glides instead of jumping, the broom and its dust stay in step however long the
  run takes, and the figures that used to sit at the bottom now flank the ring: selection, file count,
  largest item, what is in use, what is only partly covered.
- The Microsoft Store row shows a Store icon instead of a generic box.
- The two extra themes are back to changing only the accent colour, in slightly reworked shades.
- Settings dropdowns line up flush with the switches above them and have a proper opening button.
- Installer: the description under each setup type starts flush beneath its title.

### Fixed (2026-07-26, code review)

- The log no longer contains your account name. Package managers print absolute paths in their output,
  and a failed install is logged with that output, so `C:\Users\<name>\...` ended up in the file people hand
  over when asking for help. Paths are still logged, with the profile directory replaced.
- A cleanup target that failed left its row showing a running progress bar for ever; a cancelled run showed
  a full ring as though it had completed.
- An unexpected error during a cleanup now shows a message instead of ending the app.
- Resolving install locations after a scan no longer probes the disk for every row at once, and no longer
  looks each row up twice.

### Added / Fixed (2026-07-26, third review round)

- Optional virus check on downloads. Files the app fetches itself (the self-update artifact and the
  NVIDIA driver installer) are handed to the installed antivirus before they are run: Microsoft Defender
  by file path, or AMSI when another product has taken over. A detection stops the install and says so; no
  antivirus, or one that cannot answer, never blocks an update. On by default, switchable in Settings.
  Package managers verify their own downloads and are unaffected.
- Cleaning Temp no longer crashes the app. A single-file .NET app loads its native libraries out of
  `%TEMP%\.net` for its whole life, so emptying Temp deleted this app's own DLLs while it was running. That
  extraction root (every application's, not just ours) is now off limits, as is the app's own data folder.
- The system repair step is two separately selectable entries, `sfc` and the DISM sequence, so the long
  one is no longer forced on you to get the short one.
- Cleanup asks for administrator rights before measuring. Without them `sfc`, DISM and Disk Cleanup did
  nothing at all and Windows' own temp folders could be neither measured nor emptied in full. Those targets
  now run in one short-lived elevated helper (a single prompt for the whole dialog). Declining is a normal
  answer: the run continues unelevated and the affected rows say so.
- Every cleanup row now states a size or why there cannot be one: a blank cell read as "not detected".
- Cancelling a scan takes effect at once instead of waiting for the slowest source to notice.
- "Start" no longer freezes the window (launching went through the UI thread) and now works for
  ordinary winget/Chocolatey packages, which register no launch icon: the program's own executable is
  looked for in its install directory, conservatively enough never to start the wrong one.
- The critical badge sits directly beside the name and is larger.
- Settings dropdowns are narrower, with a proper full-height button at their right edge instead of a small
  chevron; the factory-reset button is quietly red rather than a solid red slab.
- The "AI enthusiast" theme is violet throughout, with a gradient window and no grey-green cast.
- Dialogs close when you click the main window beside them, and stay open when you switch to another
  application; previously, either would close them.
- The cleanup dialog's select-all button states what the click will do, like the welcome dialog's.

### Changed / Fixed (2026-07-25, second review round)

- winget now shows everything `winget upgrade` shows. The scan asked the PowerShell module, which
  decides for itself what counts as an update and left some out; it now reads winget's own upgrade list and
  only falls back to the module if winget cannot be run at all.
- pipx tools, VS Code extensions and GitHub CLI extensions now report a real target version, release date,
  size and project page (from PyPI, the Marketplace and GitHub). Android SDK packages and .NET workloads
  have no such feed and say so rather than showing invented values.
- Cleanup: a new "Repair Windows system files" step (on by default) runs `sfc /scannow` and the DISM repair
  sequence, and every entry now also reports what it could NOT delete because a file was in use, so the
  figures add up instead of quietly missing the locked files.

### Changed / Fixed (2026-07-25, review round)

- The welcome screen shows the app's icon, its button now says what it will do ("Clear selection" /
  "Select all"), and it never appears during a build or test run.
- Installer: a portable copy is suggested in a folder of its own next to the installer, and the
  Standard/Portable choice is measured again when the page is shown so it really is centred.
- Table: the "critical" mark moved to the right of the name; Steam's "1 update pending" row is the same
  height as a category header and carries an update icon; ignored rows say whether one version or the whole
  package is ignored; a removed row reports "Uninstalled" in red instead of a green "done"; **Start now
  works for ordinary packages** (the program's own executable is resolved from the uninstall registry).
- The manual-update category no longer has a pill of its own at all, and ticking everything from the header
  or the bottom button now visibly ticks the category checkboxes too.
- Themes: "Hacker" and "AI enthusiast" tint the whole window, cards and controls rather than only the accent.
- Every dialog closes when you click away from it (except a confirmation, and never mid-cleanup).
- Settings: one "Reset everything" button replaces the separate reset and delete-data pair, and all three
  dropdowns were rebuilt: taller, wider, with a checkmark on the current value and a proper floating menu.
- Cleanup: the ring no longer breaks on small figures, each entry shows its own progress with a bar,
  percentage and "x MB / y MB", Windows' Disk Cleanup runs unattended from a safe handler list, the sweep
  animation has drifting dust, and the button moved to the right of the toolbar, left of Export.
- Typing in the search box no longer stutters the table (the filter settles for a moment first), and the
  row animation is capped at 60 fps.

### Fixed (2026-07-25, hang fixes)

- The app no longer comes up as a tray icon with no window. Detecting which update sources are present
  ran on the UI thread and waited for every probe, including one that starts `wsl.exe` with a two-minute
  timeout, so the window could not appear until that finished. Detection now runs in the background, is
  cut off after four seconds no matter what, and a source that has not answered by then is simply offered.
- The test run finishes again. Any error while rendering a window opened a confirmation dialog that
  waited for a click nobody could give, which left `build.bat` running for ever. Headless runs no longer
  raise dialogs at all.

### Changed / Fixed (2026-07-25 round)

- The table no longer flashes when it refreshes. A scan now reconciles the rows it already shows
  instead of clearing the table and rebuilding it, so rows stay put through a rescan; new rows ease in.
- The scrollbar glides over every row instead of jumping a whole category at a time, and the table
  can no longer be scrolled past its last row into empty space.
- "Ignore this version" / "Ignore all versions" replace the old ignore entries and mean exactly
  that: ignoring a version hides only that version, so an update that keeps reappearing after it
  installs (winget reports some that way) stays gone until a genuinely newer one arrives.
- Stores, launchers and Steam are now one "Manual update" category, always at the very bottom, with
  a single pill of its own, and a "Hide manual" toggle that removes both the category and its pill.
- Every message and confirmation is now the app's own themed dialog instead of the system message box,
  including the uninstall warning (two clear lines) and the crash report (with a copyable detail panel).
- The row context menu is available on every row, opens with **Start**, and is no longer see-through;
  ignored rows sink to the bottom of their category and their mark sits to the right of the name.
- Table header: shorter, titles vertically centred and as large as the category titles, Date/Size/Version
  centred over their columns, and the select-all checkbox at the far left. Each category shows an icon.
- Settings: all three dropdowns are taller and fully opaque, the source list is inset evenly on both
  sides and its collapse chevron no longer touches the card edge.
- Select all really selects everything, then turns into "Clear selection"; the "nothing selected"
  text is gone while the count and size stay.
- The tray menu is rounded and matches the app; the toolbar buttons and their icons are larger.
- Theme changes now reach every open window, so nothing is left painted in the previous theme.
- Installer: Setup comes to the foreground, the Standard/Portable choice is truly centred, and the
  "choose how…" line is aligned with the page title.

### Changed / Fixed (2026-07-26 round)

- Switching a filter only touches the rows that actually change. The table used to be a filtered view
  over every row, and re-running that filter told WPF to reset the whole list, so hiding one category
  rebuilt and re-animated rows that had not moved. The visible set is now maintained directly, so a filter
  change is the handful of additions and removals it really is, and the rows that stay are left alone.
- A theme change reaches every window, not just the main one. A window's backdrop is painted from the
  theme that was current when it opened and does not follow a resource change, and only the main window
  was ever registered, so switching to the light theme in the settings window relit every card on it and
  left the window itself dark. Every open window is now repainted, including confirmations.
- Stopping an update batch stops it now. The elevated helper had no way to be told to stop, so a stop
  waited for every remaining package in the batch. It now takes a stop over the same pipe, kills the tool
  it is running, and leaves the rest of the batch unstarted, and unreported: nothing that never ran is
  written down as a failure.
- The cleanup dialog can be held and stopped while it runs. Pause holds between files, Stop ends it at
  once, and both reach the elevated half of the run as well. The one thing Stop cannot cut short is a
  repair step already under way (killing sfc or DISM mid-write can damage the component store), so that
  one ends after the current step.
- The cleanup ring is round again. A part-filled arc was being centred on its own bounds instead of on
  the circle, and its radius did not match the track it was drawn over, so the fill leaned off-centre at
  every value below 100 %.
- The cleanup animation is new: a sweeping broom with sparkles that twinkle in turn, on one shared period
  so it cannot drift out of step. "Nothing found" is quiet and grey; the two sentences explaining why Disk
  Cleanup and the repair pass cannot report a size are gone.
- The row and toolbar buttons say **"Update"** rather than "Install"; that is what they do.
- The install strip no longer reports free disk space; it was the one figure that had nothing to do with
  the batch.
- The settings dropdowns are as wide as their longest entry and no wider, and the chevron sits in the same
  colour as the rest of the box.
- Android SDK is found where it actually is: `ANDROID_HOME`, `ANDROID_SDK_ROOT` and versioned
  `cmdline-tools` folders, not only `cmdline-tools\latest`. When Android Studio is installed but those
  tools are not, a row now says so and opens Studio, naming the component that makes updates automatic.
- Candidate list for future sources: `docs/SOURCE-CANDIDATES.md` (32 researched, with what each can
  actually report).

### Added (2026-07-26, sources round)

- A source never duplicates another one. A tool that can update itself only gets a row when nothing else
  is going to update it: `PackageManagerOwnership` checks which directory its executable resolves from, and a
  binary inside winget's package store or Links folder, Scoop's apps or shims, or Chocolatey's lib or bin
  belongs to that manager. **Git for Windows is deliberately absent**: it registers an uninstall entry, so
  `winget upgrade` already offers it (reported as a duplicate row); the standalone .NET SDK is absent for the
  same reason.
- Deno, Bun and uv update themselves, each shown only when no package manager owns it. On the dev host
  that is exactly what happens: Deno and uv come from winget and stay quiet, Bun was installed by its own
  script and gets a row.
- uv tools (`uv tool list`): asks PyPI for the newest version of each tool and lists only what is
  genuinely behind, never every installed tool as a supposed update.
- The Microsoft Store's own winget source is now queried as well and merged by id, because a Store app
  that was never matched to a winget manifest is not in the default upgrade list.
- A source test bench (`SourceProbeTests`, off unless `WU_PROBE=1`): runs every source's real probe and
  real scan on the machine it is started on and reports detected / rows / time / error per source, plus a
  raw-output dump for one tool (`WU_PROBE_EXE`). It found two defects on its first run: see below.

- Google Cloud CLI components (`gcloud components list --format=json`): reports the installed and newest
  version of every component and its download size in bytes, so those rows carry a real size with no extra
  lookup. Only installed components that are genuinely behind become rows; `gcloud components update` moves
  them all forward together, so the row's id is not an argument. Verified against real output on a clean VM.
- Six further candidates were installed on a clean VM, run, and **rejected with the evidence recorded** in
  `docs/SOURCE-CANDIDATES.md`: yarn global and helm plugins report no target version at all (every row would
  be a phantom update), conda cannot run non-interactively without accepting Anaconda's Terms of Service on
  the user's behalf, pnpm global refuses until the user has run `pnpm setup`, Ollama needs a running server
  and a model tag has no version, and nvm/juliaup/GHCup treat "update" as installing an additional version
  side by side rather than moving an installed one forward.

- Ollama models, with a real staleness signal rather than a guess. A model tag carries no version, so
  listing every model as an "update" would be a phantom row; instead each model's manifest digest is compared
  with the digest the registry serves for the same tag. Verified end to end on a clean VM: for
  `all-minilm:latest` the local API and the registry's `ollama-content-digest` header agreed bit for bit, and
  the table showed no row. The scan speaks Ollama's local HTTP API (digest, byte size and pull date in one
  document); the source reports itself unavailable when no server is running, and never starts one. Only
  models from Ollama's own library are checked; a name from a user namespace or another registry is refused
  rather than turned into a URL.

### Fixed (2026-07-26, found by the source probe)

- Bun offered an update to the version already installed. Its releases are tagged `bun-v1.3.14`, and
  comparing that against an installed `1.3.14` parsed as neither version, so the comparison fell through to
  "the strings differ" and produced a phantom row. A release tag is now read for the version inside it.
- The Android SDK is found through `ANDROID_HOME` and `ANDROID_SDK_ROOT` as well as the default path, and
  `sdkmanager` is given its SDK root explicitly rather than relying on the environment; without it, the tool
  refuses to run at all. Verified against a real SDK at a non-default location.

### Added (2026-07-26, second round)

- One row per update, however many sources offer it. A package reachable through two sources, e.g.
  graphify installed through pip and through uv's tools at the same time, now takes a single line that names
  every source it came from, best first, and the best-ranked one carries out the update. The ranking is an
  explicit table (`SourceRank`): the OS's own updater, then the general Windows package managers, then the
  ecosystem that owns the package, then toolchain managers, and anything that only updates itself last.
  What counts as "the same update" is deliberately strict: same package, same installed version, same
  version on offer, so two real installations at two different versions keep their own rows.
- The cleanup dialog says what it is doing. Each running target shows the file being deleted with its
  full path, or the repair command and the step it has reached, under its own progress bar. Targets that are
  picked but not started yet show a turning ring on the right; so does a row in the update table that is
  waiting its turn.
- Several cleanup targets run at once where that is safe and useful: everything that only deletes files
  runs together, while Disk Cleanup and the two repair passes still run strictly one at a time and in
  catalogue order (DISM repairs the store that sfc restores from).
- The cleanup dialog can be resized and stays laid out: the gauge band keeps its proportions and stays
  centred, and only the target list takes the extra room.

### Fixed (2026-07-26, second round)

- Pressing any button turned its label and icon black. The cause was in WPF-UI, not in this app's
  styling: `ui:Button` declares its own `PressedForeground` and defaults it to the system control-text
  colour, plain black, while nothing in the shipped style ever sets it. Now set once, app-wide, for every
  appearance.
- The window buttons in the cleanup dialog did not react to the mouse. WPF-UI drives that highlight only
  from a win32 message hook, so wherever the hook does not get installed the buttons never light up. They now
  also follow WPF's own mouse-over, with the same brushes, so the two paths agree.
- DISM reported a flat third per step. Its progress bar is redrawn with backspaces and carries a real
  figure, which is now read (in both `.` and `,` decimal spellings, because DISM prints in the machine's own
  language) and throttled to whole percent.
- Narrowing the main window pushed Settings, History and Export off the right edge. The toolbar's action
  buttons are now measured first and keep their space; the search box and the filter switches wrap onto a
  second line instead.
- Re-opening the cleanup dialog during a scan left a broken ring. Closing it used to cancel the measuring
  pass, and the next open built a second view model whose scan raced the first one's remaining targets. The
  dialog now re-attaches to whatever is already running, and the ring starts where that work already is.
- Updates of a package installed for the current user failed: Bitcoin Core through winget was the
  example. winget refuses the *requested operation* from an administrator context on a user-scope package,
  and the elevated helper always is one; the retry that already existed for removals now covers installs too.
- The Android SDK was found, but the wrong one. With `ANDROID_HOME` pointing at one SDK and Android
  Studio using another on a different drive, `sdkmanager` was pointed at the wrong tree and reported nothing
  to do. The SDK Studio itself records (in its own `jdk.table.xml`) is now the first answer, ahead of the
  environment variables, and a Studio-style `<dir>\SDK` layout is probed as well. Verified against the real
  split installation.
- The settings dropdowns lost the chevron segment and its divider, and are sized to their content.
- Every installed VS Code extension was reported as a pending update. `code --list-extensions` says
  nothing about what is newest, and the source turned each installed extension into a row regardless: four
  extensions, four phantom updates. The Marketplace is now asked during the scan, in parallel, and only an
  extension that is genuinely behind gets a row. Measured on this machine: four rows became none, correctly.
- An implicit style must not derive from the implicit style of its own target type. `BasedOn="{StaticResource
  {x:Type ui:Button}}"` inside an implicit `ui:Button` style is a self-reference that WPF resolves to nothing,
  which silently stripped WPF-UI's template from every button in the app: no icons, no accent fill. Caught in
  a render snapshot before it shipped; the derived styles now name WPF-UI's keyed base style, and the title-bar
  hover state (which has no keyed base to name) is built in code instead.

### Fixed (2026-07-26, third round, mostly found in real usage logs)

- The cleanup dialog's ring was broken by a threading bug, not by a geometry one. Every reply from the
  elevated helper was handled on the pipe's reader thread and went straight on to touch the window's
  animated `DisplayPercent`, so each one threw "the calling thread cannot access this object", the ring never
  moved, and the log filled with `An elevated reply could not be handled`. Replies are now marshalled onto
  the dispatcher.
- Installing and cleaning no longer block each other. The elevated helper had ONE slot for all
  machine-changing work; it now has one lane per kind of
  job, so two installs still serialise and two cleanups still serialise, but an install and a cleanup run
  side by side.
- A winget update that changed nothing reported success. `0x8A15002B` ("no applicable upgrade") was
  counted as done, but every command names one exact package the scan had just seen an upgrade for, so it
  means winget declined, most often because the package belongs to the user and the caller is an
  administrator. Bitcoin Core went green and stayed at 31.0.0 because of it. It is now retried unelevated
  once and then reported honestly.
- A 22-second restore point looked like a hang. It is created before every install batch and said nothing
  while it worked; the strip now names the step.
- The Android SDK update failed at the very end (`100% Unzipping… exit code 1`): `sdkmanager --update`
  also updates `cmdline-tools;latest`, which is the copy it is running from. It is now started from a
  versioned `cmdline-tools` folder instead, so the files being replaced are never the ones in use. The folder
  choice also compares versions as numbers; sorted as text, "9.0" beat "21.0".
- Setup opened behind the window that launched it. Toggling always-on-top on and off in consecutive
  statements is a no-op, and `BringToFront` alone loses to Windows' foreground lock; the wizard is now held on
  top until it has actually been activated.
- Finished rows leave the table when a new scan starts, so a completed update does not sit there until
  the scan happens to contradict it. Failures stay; their Retry and Details are the point.
- The cleanup overview now carries real figures on both sides, and "Aktuell" shows the running command, that
  command's own percentage, and how much of the current target is done.
- The app icon is a blue diagonal gradient in the family of the accent the UI uses, lit from the top left.

### Changed (2026-07-27, packaging)

- The single-file executable is no longer internally compressed. Compression left a binary with no
  readable strings and the entropy profile of a packer, which is what generic machine-learning detections
  are built to flag, the most common cause of false positives on self-contained .NET apps. The portable
  exe grows (81 MB → 197 MB); the **installer shrinks** (144 MB → 100 MB), because Inno's LZMA2 compresses
  an uncompressed payload much better than an already-deflated one. Startup is marginally faster.
- A full Win32 version block: file description, product description, clean product version (no commit
  hash glued to it). An anonymous binary is half of what earns an unsigned file a heuristic hit.
- Code signing support in `build.bat`, opt-in via `WU_SIGN_THUMBPRINT`: both executables are signed
  before the installer wraps them, and the Setup afterwards. Nothing about the certificate is stored in the
  repository. Without the variable the build carries on and says the artifacts are unsigned.

### Added (2026-07-27, second round)

- A cleanup history. Every run is recorded: when, what it freed, and how much each single area gave
  up, and shown in the cleanup dialog as a collapsed list that expands per run. A cleanup cannot be
  undone, so this is the only record of what left the machine. Kept to the 50 most recent runs.
- Five more cleanup areas: shader caches (D3D, NVIDIA, AMD, Intel), package-manager caches (pip, npm,
  Yarn, NuGet, Chocolatey), build-tool caches (Gradle, Android SDK), kernel crash dumps and Windows' own
  log folder. The last two start switched OFF: they are exactly what someone diagnosing a blue screen or
  a failed update needs. No browser data anywhere in the catalogue, by design.
- A "rescan" button in the cleanup dialog, because the figures are a snapshot of a machine that keeps
  working.

### Fixed (2026-07-27, second round)

- Accent-filled buttons are no longer labelled in black. Fluent's dark theme fills them with a pale
  tint of the accent and writes the label and icon in `#000000`, measured on the shipped build, at rest
  as much as pressed. The accent surface is now painted with the accent itself, deepened only as far as a
  white label needs to reach 4.5:1, and the label is white. Three earlier attempts missed because they
  reasoned about which style setter wins; this one reads the resolved colour, and a test asserts it.
- A failed winget ladder reports every rung, not just the last. The queue decides from those codes
  whether to retry the job without administrator rights, and the deciding code is the FIRST rung's,
  reporting only the last one threw it away, which is why Bitcoin Core still failed. The ladder now also
  stops as soon as an elevated attempt hits a user-scope refusal, instead of spending two more attempts on
  something no argument can fix.
- Stopping a cleanup now stops Disk Cleanup too. It was deliberately left running, so the dialog said
  the run had ended while Windows carried on deleting. The one step still allowed to finish is a system
  repair (killing DISM mid-restore can damage the component store), and the dialog now says so.
- Release dates and download sizes appear one at a time, as each is found, instead of all at once at
  the end: the registry-and-disk pass no longer blocks the network lookups behind it.
- An update reported twice in one scan produces one row. A row created earlier in the same batch was
  not in the table yet, so replacing it removed nothing and both copies appeared. Windows Update hits
  this regularly.
- Scanning and updating at the same time is allowed. The block dated from when a scan cleared the
  table; it reconciles now, and leaves queued and running rows alone.
- A source that has been found is not probed again every scan; the probe costs a process launch for
  some of them. A source that was ABSENT is still probed every time, so a tool installed a minute ago
  appears at once.
- The history window's filters sit beside the search box instead of under it.
- The antivirus setting now says what it actually covers: files this app downloads itself, today the
  graphics-driver installer. winget, Chocolatey and the Store verify their own downloads and never reach
  it, which is why the scan appeared to do nothing.

### Fixed (2026-07-27)

- winget upgrades now use `--include-unknown`, like the scan always did. The two commands disagreed about
  what was upgradable, and winget said so in as many words: "The version number of this package cannot
  be determined. To upgrade anyway, add … --include-unknown" (PokeMMO, German output).
- A refused upgrade is retried according to what winget actually said, instead of once, blindly: the plain
  upgrade, then the same pinned to the user scope ("a newer package version is available, but it does not
  apply to your system" (Bitcoin Core, installed per-user), then `install` instead of `upgrade` for
  packages with no upgrade path at all. Escalation only happens for refusals another attempt can address; a
  genuine failure is still reported at once. Every rung stays silent and non-interactive.
- Cancelling a scan returns immediately. It used to wait for the slowest source to notice, and not every
  source can: Windows Update scans through a COM call that takes no token and runs on for another quarter of
  a minute. A cancelled run is now abandoned, and the abandoned sources stop feeding the table, so nothing
  they still produce can reach the next scan.
- Scanning and updating run at the same time. Queueing an install no longer refuses while a scan is
  running, and the scan's own clean-up leaves queued and running rows alone.
- The toolbar buttons never disappear. Cleanup, Export, History and Settings stay reachable during a scan
  and during an install; only the search/filter block is swapped for the live install strip.
- A package found in several sources is one line everywhere: in the table and in the install strip,
  while both real installations are still updated.
- The cleanup dialog is wider, its rows are cards that light up under the mouse and while they work, and a
  target still being measured shows three pulsing dots instead of a static "…".

### Security

- Background enrichment makes read-only GET requests to `pypi.org` and `registry.npmjs.org` only, with
  the package name URL-escaped, a response-size guard, and no credentials. Registry responses are
  parsed defensively as hostile input and never fault the scan.

- The downloaded NVIDIA installer is run only after its Authenticode signature is valid AND its signer
  is NVIDIA, and its URL is re-checked against NVIDIA's own hosts at install time. A signature alone
  only proves "signed by someone". The driver download and install run unelevated in the main app; the
  vendor installer raises its own elevation, so the elevated worker (which writes no files) is never
  involved.
- Uninstall travels the same authenticated pipe and single-UAC elevated worker as install; package
  names are still refused as arguments when they could be read as options.

- The elevated install worker no longer takes an executable path from `%PATH%`. winget is reachable
  only through `%LOCALAPPDATA%\Microsoft\WindowsApps`, which any process running as the user can
  write, so a binary planted there would have run with the administrator rights the user granted to
  this app. An elevated launch now requires a signature, catalog or embedded, since every tool
  involved turns out to be catalog-signed, or the genuine app-execution alias, which cannot be
  forged by writing a file.
- The worker checks which process serves its pipe, as the app already checked which process connects
  to it. Without that, an abandoned worker could be handed a job by anyone who claimed the freed pipe
  name.
- The worker writes no files at all. It used to log into `%APPDATA%`, the data folder beside the exe
  and `%TEMP%`, all replaceable by a reparse point aimed where only administrators may write. Its
  log now travels back over the pipe and is written by the unelevated app.
- A verified self-update is held open from before the hash until after it is started, so the file
  cannot be swapped between being verified and being executed.
- Update metadata and the checksum list are read with a size limit, Steam app ids are required to be
  numeric before they reach the shell, and a squatted single-instance pipe can no longer swallow every
  launch of the app.

### Fixed
- Starting a scan during an install emptied the table and silently dropped the rest of the batch,
  which kept installing invisibly. Scanning is refused while an install runs.
- Install progress was matched by item id alone, so a package offered by two sources (git, python,
  7zip exist in both Chocolatey and Scoop) reported both installs onto one row and left the other
  stuck on "queued". Status is routed by source and id.
- A failed self-update download took the whole app down instead of reporting the failure, and the
  download's own 100 second limit made that the normal outcome for a 100 MB artifact.
- Cancelling an install recorded packages as failed in the history although they had finished.
- A failing restore point aborted the entire batch it was only meant to precede.
- Ignoring an update, or clearing the ignore list, left the headline count stale.
- The interval notification counted rows that cannot be installed, so it announced updates forever on
  a machine that was up to date.
- Closing the window during an install ended the app and abandoned the batch.
- The focus listener died permanently on the first unexpected error, after which every launch claimed
  the app was already running instead of raising its window.
- Light theme was never actually rendered by the test that existed to prove it, and the Settings
  window's own starting height hid its last group. Both are now shown by the snapshots.
- Screen readers announced the row checkbox and the toolbar buttons as unlabelled controls, and Tab
  could not reach a row's own controls.
- Symbol files are no longer copied into `dist\`; they embed absolute source paths and that folder is
  what gets published.

### Added (2026-07-30)
- New source `github-release`: detects updates for programs installed outside any package manager
  whose Add/Remove Programs entry names a GitHub repository, checked against that repo's latest
  release. Tier C (detect and open): no universal silent-install switch exists for an arbitrary
  third-party installer, so it never auto-installs.

### Fixed (2026-07-30, Phase 1)
- Installer no longer starts behind whatever window launched it (stronger `AttachThreadInput`-based
  foreground fix; this had regressed twice before).
- Pipe EOF now cancels active elevated installs/cleanups instead of leaving them running unobserved.
- Explicit-null settings sections no longer crash on startup; factory reset now actually resets
  everything, including the download-scanning preference.
- A remotely-supplied URL (e.g. a vendor API field) can no longer invoke an arbitrary registered
  shell protocol; only http(s) and this app's own fixed launcher schemes are allowed.
- Retrying a failed uninstall no longer performs an install instead.
- The NVIDIA installer is tracked to its real exit instead of being reported "stopped" while it keeps
  running after a cancel/timeout.
- The public self-update channel can no longer install a prerelease build.
- Windows Update installs are verified against the individual update's own result, not just the
  aggregate code; a broken WUA search is reported as an error instead of an empty "up to date".
- CLI scan sources no longer read a nonzero exit code as "nothing to update" for a source-specific
  accepted set of codes; winget specifically only treats its own documented benign codes as empty.
- Fixed the recurring duplicate-row bug: a rescan started before the previous one settled could run a
  source concurrently with itself and let stale status overwrite the new scan (scan-generation
  tagging + one lane per source).
- `.NET workloads` no longer reports every installed workload as updatable forever; it now reads a
  real target version and reports nothing when there isn't one.
- A row mid-install/queued no longer stays selectable in "select all".
- A language switch now refreshes every computed localized label across the app, not just the ones
  bound directly.
- winget gets a last-resort `--force` rung for `0x8A15002B` failures that survive every other rung.
- Steam uninstall reports a manual handoff, never a fabricated success or failure: Steam's own
  confirmation dialog is opened, but this app cannot see what happened next, and the row/history now
  say so distinctly (`RowInstallState.HandedOff`) instead of recording a cancelled removal as done.
- Row-action buttons no longer show a stray "Open" overlapping the real action label.
- Various: a leaking button-pressed-state edge case audited and confirmed already fixed; corrected
  install-strip layout clipping at the window's minimum width; Settings' "Advanced" card now has a
  real title.

### Added (2026-07-30, Phase 2 + 3)
- Main table: Date/Version/Size reordered and centered to match the header.
- Current-install strip: shows the version being installed to and its release date; total
  size/downloaded/speed are now three always-legible lines instead of one crowded string; Pause and
  Cancel are docked so they can no longer be squeezed off the edge at a narrow window width (the cause
  of Pause sometimes being unreachable).
- Collapsible queue panel below the current-install strip, collapsed by default: lists every other
  queued item with a drag-and-drop reorder and a remove (X) button, both of which change the actual
  install order/queue, not just the visual list.
- Source pills: a failed source's hover tooltip now explains what actually went wrong (the raw reason),
  not just "scan failed".

### Fixed (2026-07-30, Phase 3 + ad-hoc)
- Windows Update rows are now marked critical when Windows Update itself leaves the severity blank
  (common on cumulative updates) but classifies the update as a Security Update, so a missing rating is
  no longer read as "not critical".
- Android SDK detection now also recognizes an install where the SDK lives directly in Android
  Studio's own install folder, not only in a nested "SDK" subfolder.
- Tray icon extraction failure now always falls back to a visible generic icon instead of possibly
  leaving the tray icon blank.
- Log files are capped at 5 MB regardless of the calendar day (previously day-only rotation let one
  reach 13.7 GB across a long dev session) and the rolled-out file is compressed into `logs\archive\`
  as a `.zip` instead of just growing forever; the existing log-retention cleanup now prunes that
  archive too.

### Added (2026-07-30, Phase 4)
- The error-detail dialog now colours a failed install/uninstall's raw output instead of showing it as
  a flat text dump: bracketed rung labels (e.g. `[upgrade]`) in accent, exit codes in red or orange
  depending on whether it is one this app already knows how to retry, tool names in bold. The raw text
  itself is unchanged; Copy, history and the log all still get the plain string.
- A liability disclaimer (this tool does not vet third-party update content, use at your own risk) is
  shown on install (a read-through page, DE/EN) and in the README.

### Fixed (2026-07-30, Phase 3 wrap-up)
- A unit test was calling the real Steam shell handler on every `dotnet test`/`build.bat` run, popping
  Steam's own uninstall confirmation for a real app id. `SteamSource` now takes an injectable launch
  delegate; the test uses a no-op stand-in.
- `WindowLayoutTests`' install-strip check was fixed after the Phase 3 rename (the strip's root changed
  from a `Grid` to a `DockPanel`) and, along with `LiveButtonContrastTests`, moved out of `build.bat`'s
  gating test step into its warning-only step; both share the same environment-dependent WPF `pack:`
  URI registration that is known to fail in a scripted run even when a plain `dotnet test` passes clean.
- Android SDK detection now also recognizes an install where the SDK lives directly in the install
  folder (no nested `SDK` subfolder).
- `uv` no longer shows twice (its own source/winget, plus a redundant "manual" row from the generic
  GitHub-release source). That source now knows which repos a dedicated source already covers.
- Error tooltips (a failed source's pill, a failed row's warning icon) now open in ~150ms instead of
  the ~1s default, carry the tool's actual last output line instead of just an exit code, and can be
  copied to the clipboard with a right-click.

### Notes
- winget is driven through the official PowerShell module (or the CLI as a fallback) rather than its
  COM API, which is not usable from an unpackaged, unelevated process. Installs still run through
  winget itself, so its manifest hash verification is unchanged.
