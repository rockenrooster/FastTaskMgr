# Changelog

All notable changes to FastTaskMgr are documented here.

## Unreleased

- Add upcoming user-visible changes here before tagging a release.

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
