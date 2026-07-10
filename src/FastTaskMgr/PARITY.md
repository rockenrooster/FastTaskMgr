# FastTaskMgr Parity Matrix

Target: Windows 11 Task Manager behavior on Windows 11 x64. Status values track FastTaskMgr implementation, not Windows capability.

| Windows Task Manager feature | FastTaskMgr status | API/source used | Notes |
| --- | --- | --- | --- |
| Processes page shell | Done | WinForms virtual list | Left nav, search, command bar, virtual rows. |
| Apps/background/Windows process grouping | Partial | Process snapshot + metadata | Exact Windows grouping is undocumented; first pass shows a flat list. |
| Process tree | Partial | Parent PID from SystemProcessInformation snapshot | Parent PID is collected for every row; expand/collapse tree UI is not implemented yet. |
| Process name | Done | NtQuerySystemInformation snapshot | Single syscall for all processes per tick. |
| PID | Done | NtQuerySystemInformation snapshot | Direct process ID. |
| Process status | Done | Snapshot thread states + IsHungAppWindow | Running, not responding, and suspended (all threads wait-suspended). |
| CPU percent | Done | Snapshot kernel+user time deltas | Keyed by PID plus create time to survive PID reuse. |
| Memory usage | Done | Snapshot working set | Access denied rows degrade gracefully. |
| Disk throughput per process | Partial | Snapshot I/O transfer counters | Sortable process I/O byte delta with no per-process handle opens; exact Task Manager disk-only accounting likely requires ETW. |
| Network throughput per process | Not possible yet | ETW candidate | Public low-overhead per-process accounting is limited. |
| GPU usage per process | Not possible yet | PDH GPU Engine counters candidate | Not implemented; counter names vary by OS/GPU driver. |
| Power usage | Not possible yet | Heuristic candidate | Not implemented; will only be shown as an approximation. |
| Process icons | Partial | Shell extraction candidate | Not implemented in first pass. |
| Search/filter processes | Done | In-memory row filter | Name, PID, and path. |
| Sort process columns | Done | Virtual list sort | Column sorting with typed keys for numeric columns. |
| End task | Needs elevation | OpenProcess + TerminateProcess | Works when permitted; protected/elevated targets may deny access. |
| Restart Explorer | Done | Terminate explorer + ShellExecute explorer.exe | Explorer only. |
| Open file location | Done | Explorer select command | Disabled when path unavailable. |
| Properties | Done | Shell properties verb | Disabled when path unavailable. |
| Search online | Done | Default browser URL | Only user-triggered network action. |
| Go to details | Done | In-app navigation | Context menu jumps to Details and selects the row. |
| Efficiency/suspended status | Partial | Snapshot thread states | Suspended status is shown; setting efficiency mode is not implemented. |
| Performance page shell | Done | WinForms custom drawing | Clickable sidebar tiles and detail graphs. |
| CPU utilization graph | Done | GetSystemTimes + NtQuerySystemInformation | Total CPU plus per-logical-processor detail graphs. |
| CPU speed | Partial | CallNtPowerInformation | Base/max MHz shown; live current speed is not shown yet. |
| CPU processes/threads/handles | Done | GetPerformanceInfo | System aggregate values. |
| CPU uptime | Done | Environment.TickCount64 | Straight OS uptime. |
| CPU base speed/sockets/cores/logical processors | Done | CallNtPowerInformation + GetLogicalProcessorInformation | Base speed, sockets, cores, logical processors, virtualization, and cache totals are shown. |
| Memory usage graph | Done | GlobalMemoryStatusEx | Rolling sample window. |
| Memory total/available | Done | GlobalMemoryStatusEx | Direct OS values. |
| Memory committed/cached/pools | Partial | GetPerformanceInfo | Commit/cache shown; pool details not shown yet. |
| Disk performance pages | Partial | DriveInfo + PDH counters candidate | Capacity/free space and sidebar tiles are shown; active time and throughput are not implemented yet. |
| Network adapter pages | Partial | NetworkInterface byte deltas | Sidebar/detail throughput shown; exact Task Manager adapter details are not complete. |
| GPU performance pages | Not possible yet | PDH GPU counters candidate | Not implemented in first pass. |
| Update speed | Done | App setting + sampler interval | Paused/low/normal/high. |
| Startup apps page | Done | Registry + startup folders | HKCU/HKLM Run and startup folders. |
| Startup enabled/disabled | Partial | Conservative move/restore | FastTaskMgr can toggle supported entries; StartupApproved parity is not complete. |
| Startup impact | Not possible yet | Local heuristic/history candidate | Exact Windows impact source is undocumented. |
| Users page | Done | WTSEnumerateSessions + snapshot session IDs | Per-session process count, CPU, and memory rollup with disconnect/sign off actions. |
| Details page shell | Done | WinForms virtual list | Flat process table. |
| User name | Partial | Process token + LookupAccountSid, cached per process | Access denied rows are blank. |
| Architecture | Done | IsWow64Process2, cached per process | x86/x64/ARM64 where API succeeds. |
| Description/company | Done | FileVersionInfo, cached per path | Description and Company columns. |
| Command line | Done | NtQueryInformationProcess(ProcessCommandLineInformation) | Cached per process; blank for protected/elevated targets. |
| UAC virtualization | Not possible yet | Token APIs candidate | Not implemented in first pass. |
| Priority | Done | Snapshot base priority / SetPriorityClass | Setting may need elevation. |
| Affinity | Done | Get/SetProcessAffinityMask | Displayed and editable via the affinity dialog. |
| Handles/threads | Done | NtQuerySystemInformation snapshot | Per-process counts for every row. |
| End process | Needs elevation | OpenProcess + TerminateProcess | Works when permitted. |
| End process tree | Done | Parent PID descendant walk | Ends the target and all live descendants; protected targets may deny access. |
| Set priority | Needs elevation | SetPriorityClass | Works when permitted. |
| Set affinity | Done | SetProcessAffinityMask | Affinity dialog with per-CPU checkboxes; may need elevation. |
| Analyze wait chain | Not possible yet | Wait Chain Traversal API candidate | Not implemented in first pass. |
| Create dump file | Not possible yet | MiniDumpWriteDump candidate | Not implemented in first pass. |
| Services page | Done | Service Control Manager APIs | Direct P/Invoke, no System.ServiceProcess package. |
| Service name/PID/description/status | Done | EnumServicesStatusEx + QueryServiceConfig2 | PID available only when running. |
| Service group | Done | QueryServiceConfig load-order group | Cached per service name. |
| Service start/stop/restart | Needs elevation | StartService/ControlService | Restart waits for the service to stop before starting; many services require admin. |
| Open Services console | Done | ShellExecute services.msc | User-triggered. |
| Go to details for service process | Done | In-app navigation | Context menu jumps to Details when the service has a PID. |
| Settings page | Done | JSON in LocalAppData | `%LocalAppData%\FastTaskMgr\settings.json`. |
| Always on top | Done | WinForms TopMost | Stored setting. |
| Minimize/hide behavior | Done | WinForms window state + NotifyIcon | Hide-on-minimize with tray icon restore and exit. |
| Theme light/dark/system | Partial | WinForms colors + registry system setting | Lightweight theme only. |
| Confirm before ending process | Done | Stored setting | On by default. |
| Remember layout | Done | Settings JSON | Size, position, start page, update speed. |
| Elevation for admin-only actions | Partial | runas candidate | Access denied is handled; relaunch elevation is not implemented yet. |
| Protected/critical process handling | Partial | Critical name warning + access checks | No kernel driver; stronger critical process coverage is still needed. |
| Single-file publish | Done | `dotnet publish` framework-dependent single-file win-x64 | Main distribution path; requires installed .NET Desktop runtime. |
| Native AOT publish | Partial | Experimental publish profile | Not required; compatibility must be verified separately. |
