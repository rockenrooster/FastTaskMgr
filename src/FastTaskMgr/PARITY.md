# FastTaskMgr Parity Matrix

Target: Windows 11 Task Manager behavior on Windows 11 x64. Status values track FastTaskMgr implementation, not Windows capability.

| Windows Task Manager feature | FastTaskMgr status | API/source used | Notes |
| --- | --- | --- | --- |
| Processes page shell | Done | WinForms virtual list | Left nav, search, command bar, virtual rows. |
| Apps/background/Windows process grouping | Partial | Process snapshot + metadata | Exact Windows grouping is undocumented; first pass shows a flat list. |
| Process tree | Partial | Parent PID from NtQueryInformationProcess | Parent PID is collected; expand/collapse tree UI is not implemented yet. |
| Process name | Done | System.Diagnostics.Process | No WMI hot path. |
| PID | Done | System.Diagnostics.Process | Direct process ID. |
| Process status | Partial | Process.Responding | Shows running/not responding; suspended detection is not complete. |
| CPU percent | Done | Process CPU deltas | Computed over sample interval. |
| Memory usage | Done | Process.WorkingSet64 | Access denied rows degrade gracefully. |
| Disk throughput per process | Partial | GetProcessIoCounters | Sortable process I/O byte delta; exact Task Manager disk-only accounting likely requires ETW. |
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
| Go to details | Partial | Local navigation candidate | Details page exists; row jump is not wired yet. |
| Efficiency/suspended status | Not possible yet | SetProcessInformation candidate | Not implemented in first pass. |
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
| Users page | Partial | Stub + WTS APIs candidate | UI stub exists; WTS session/process rollup is not implemented yet. |
| Details page shell | Done | WinForms virtual list | Flat process table. |
| User name | Partial | Process token + LookupAccountSid | Access denied rows are blank. |
| Architecture | Done | IsWow64Process2 | x86/x64/ARM64 where API succeeds. |
| Description/company | Partial | FileVersionInfo | Description shown; company column not added yet. |
| Command line | Not possible yet | PEB/NtQueryInformationProcess candidate | Not implemented; protected/elevated processes may fail. |
| UAC virtualization | Not possible yet | Token APIs candidate | Not implemented in first pass. |
| Priority | Done | GetPriorityClass/SetPriorityClass | Setting may need elevation. |
| Affinity | Partial | GetProcessAffinityMask | Displayed; setting affinity is not implemented yet. |
| Handles/threads | Done | Process APIs | Per-process counts where accessible. |
| End process | Needs elevation | OpenProcess + TerminateProcess | Works when permitted. |
| End process tree | Not possible yet | Descendant walk candidate | Not implemented in first pass. |
| Set priority | Needs elevation | SetPriorityClass | Works when permitted. |
| Set affinity | Not possible yet | SetProcessAffinityMask candidate | Not implemented in first pass. |
| Analyze wait chain | Not possible yet | Wait Chain Traversal API candidate | Not implemented in first pass. |
| Create dump file | Not possible yet | MiniDumpWriteDump candidate | Not implemented in first pass. |
| Services page | Done | Service Control Manager APIs | Direct P/Invoke, no System.ServiceProcess package. |
| Service name/PID/description/status | Done | EnumServicesStatusEx + QueryServiceConfig2 | PID available only when running. |
| Service group | Partial | SCM candidate | Column exists; group is not populated yet. |
| Service start/stop/restart | Needs elevation | StartService/ControlService | Many services require admin. |
| Open Services console | Done | ShellExecute services.msc | User-triggered. |
| Go to details for service process | Partial | Local navigation candidate | PID is shown; row jump is not wired yet. |
| Settings page | Done | JSON in LocalAppData | `%LocalAppData%\FastTaskMgr\settings.json`. |
| Always on top | Done | WinForms TopMost | Stored setting. |
| Minimize/hide behavior | Partial | WinForms window state | Hide-on-minimize implemented; tray restore is not implemented yet. |
| Theme light/dark/system | Partial | WinForms colors + registry system setting | Lightweight theme only. |
| Confirm before ending process | Done | Stored setting | On by default. |
| Remember layout | Done | Settings JSON | Size, position, start page, update speed. |
| Elevation for admin-only actions | Partial | runas candidate | Access denied is handled; relaunch elevation is not implemented yet. |
| Protected/critical process handling | Partial | Critical name warning + access checks | No kernel driver; stronger critical process coverage is still needed. |
| Single-file publish | Done | `dotnet publish` framework-dependent single-file win-x64 | Main distribution path; requires installed .NET Desktop runtime. |
| Native AOT publish | Partial | Experimental publish profile | Not required; compatibility must be verified separately. |
