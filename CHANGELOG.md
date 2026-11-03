# Changelog

All notable changes to FastTaskMgr are documented here.

## Unreleased

- Add upcoming user-visible changes here before tagging a release.

## 0.1.0.45 - 2026-11-02

### Added
- Right-click a Performance tile to hide it; right-click the sidebar and use Show hidden items to bring tiles back one at a time or all at once. Hidden tiles are remembered between runs.

## 0.1.0.44 - 2026-10-30

### Added
- The Performance page shows GPU temperature, and SSD temperature and remaining life, on the tiles and in the details. Drive pools show them for each member disk. Hard drives are never polled, so a sleeping disk is not woken.
- CPU temperature appears on systems whose firmware reports an ACPI thermal zone, which covers most laptops and some desktops. It is labeled ACPI because it is a board reading rather than the CPU's own sensor.
### Changed
- CPU speed now shows the real clock including boost, the same figure as Task Manager, instead of stopping at base speed.
- Sensors load in the background only while the Performance page is on screen, so they add nothing to startup and values appear as soon as they are ready. A slow drive cannot hold up the page. SSDs are read every 10 seconds, temperatures every 2 seconds.
### Fixed
- The GPU name no longer ends with a stray character on drivers that store it with an odd length.

## 0.1.0.43 - 2026-10-23

### Changed
- The Performance page keeps sampling in the background at all times, while other pages are open and while the window is minimized or hidden in the tray, so its graphs always show the last two minutes, like Task Manager. Background sampling costs about 1 ms per update; other pages still pause while minimized.

## 0.1.0.42 - 2026-10-21

### Added
- The Startup apps page lists packaged (Microsoft Store/MSIX) apps that start at sign-in, such as Microsoft Teams, Terminal and Phone Link, and can enable or disable them the same way Windows Settings does.
### Changed
- Pages only refresh while they are visible: sampling pauses while the window is minimized or hidden in the tray, and saving settings no longer starts background refresh timers for every page.
- Process tables repaint only the rows whose values changed, and cells no longer redraw the grid lines a second time; a refresh where nothing changed no longer repaints the table at all.
- Tables draw their rows directly with GDI instead of through WinForms owner drawing, with the same colors, selection and hover states. Repainting the Processes table takes about 3 ms instead of 40-50 ms, and the UI thread spends about 6-10 ms per Processes refresh instead of 40-55 ms.
- Only the page being shown is built at startup; the others are built the first time they are opened. Together with the faster tables, the window paints its first page about 110 ms sooner (about 275 ms instead of 385 ms after launch on a 32-thread machine).
- The Performance page draws all per-core CPU graphs in one control instead of one control per core, so with 32 logical processors the CPU view repaints in about 6 ms instead of 15 ms.
- The Details page re-reads processor affinity every 5-10 seconds (and right after Set affinity) instead of opening every process on every refresh, saving about 2 ms per refresh.
- The Performance page caches drive capacity and the network adapter list (refreshed every 10-15 seconds and on network changes), cutting a refresh from about 11 ms to under 1 ms on a machine with many virtual adapters.
- Process sampling allocates about 80% less memory per refresh, and Details page user names are resolved once per account instead of once per process.
- Settings are loaded and saved with source-generated JSON and written atomically, so an interrupted save can no longer reset them.
- The Memory column on the Processes, Details and Users pages now shows the private working set, like Windows Task Manager, instead of the full working set, which also counted memory shared with other processes. Suspended processes show 0, matching Task Manager's "active private working set".
- Enabling or disabling a startup app now uses the same setting as Windows Task Manager and Settings, so the change shows up in all three. Apps disabled with an earlier FastTaskMgr version still show as Disabled, and enabling one puts it back.
- Startup apps are named after their program, as in Task Manager (for example "Java Update Scheduler" instead of "SunJavaUpdateSched"), and shortcuts show their target's name and publisher. Searching still finds an app by its registry or file name.
### Fixed
- Switching between Performance tiles leaked window handles (about 3,000 after 800 switches), which could eventually crash the app.
- End process tree could end unrelated processes whose parent PID had been recycled. End task and End process now also confirm the PID still belongs to the sampled process.
- Starting FastTaskMgr again now brings back the running window, including when it is hidden in the tray or running elevated.
- With "Always start as admin", the elevated copy could exit immediately if the original process still held the single-instance lock.
- Closing while minimized or maximized saved the wrong window size and position. The maximized state is now remembered, and positions on disconnected monitors are ignored.
- Saving settings without admin rights showed "Requested registry access is not allowed" even when the Task Manager shortcut option was unchanged.
- Refresh errors are written to `%LocalAppData%\FastTaskMgr\error.log` instead of raising an error dialog on every tick, and exiting no longer races an in-flight sample.
- CPU percentages are computed against all logical processors even when FastTaskMgr runs with restricted processor affinity.
- The selected Performance tile is readable in dark mode, and tile hover states are easier to see.
- The Services list no longer comes back empty when a service is registered while it loads, and the Startup apps page loads off the UI thread.
- The Startup apps page now lists 32-bit apps that start for all users (registered under WOW6432Node), shows apps disabled in Windows Task Manager or Settings as Disabled instead of Enabled, and no longer lists the startup folder's desktop.ini file.
- Changing a startup app that runs for all users without admin rights now says administrator rights are needed, instead of showing a registry access error.

## 0.1.0.41 - 2026-07-12

### Changed
- The Performance page now opens instantly: performance counters warm up in the background at app start, the last sample is cached for immediate first paint, and all counters are collected through a single PDH query per refresh instead of one query per counter instance (GPU sampling alone previously polled hundreds of instances every tick).

## 0.1.0.40 - 2026-07-11

### Fixed
- Disk activity now tracks physical disks, with disk models and hosted drive letters in the tile names. Fixes zero activity for virtual volumes such as StableBit DrivePool, whose I/O is only visible on the underlying member disks.
- StableBit DrivePool member disks are grouped under their pool's tile: the sidebar shows one tile per pool with combined activity (busiest member) and summed read/write, and the detail pane shows a graph plus live stats for each member disk.

## 0.1.0.38 - 2026-07-09

### Added
- Users page with per-session process, CPU, and memory rollup plus disconnect and sign off actions.
- End process tree on the Processes and Details pages.
- Set affinity dialog on the Details page.
- Go to details navigation from the Processes and Services pages.
- Command line and Company columns on the Details page.
- Suspended process status detection.
- Services group column.
- Tray icon with restore and exit when hide-on-minimize is enabled.
### Changed
- Rewrote process sampling around a single NtQuerySystemInformation snapshot per refresh; sampling is roughly 8x faster and no longer opens handles or scans windows per process every tick.
- Process CPU and I/O deltas are keyed by PID plus create time so recycled PIDs no longer inherit stale counters.
- Service descriptions and groups are cached, so Services refreshes stop re-querying config for every service.
### Fixed
- Horizontal scroll position no longer resets on refresh when a row is selected and the process count changes.
- Hide-on-minimize no longer leaves the app unreachable; the tray icon restores the window.
- Not-responding detection no longer stalls the sampler when an app is hung.
- Restarting a service now waits for it to stop before starting it again.

## 0.1.0.36 - 2026-07-03

### Changed

- Improved GPU utilization sampling to report the busiest engine type instead of summing all GPU counters.
- Moved PID later in the Processes table so the activity columns stay grouped.
- Clarified the signed updater flow and separate install/download states.

### Fixed

- Preserved horizontal scroll position during virtual table refreshes.
- Added a self-check for GPU engine aggregation.

## Earlier releases

Earlier release history is available in Git tags `v0.1.0.2` through `v0.1.0.34`.
