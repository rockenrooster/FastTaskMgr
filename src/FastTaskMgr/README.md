# FastTaskMgr

FastTaskMgr is a fast, lightweight Windows 11 Task Manager alternative built with C#, .NET 10, and WinForms.

Tagline: Fast. Lightweight. Native.

## Current Scope

The first pass implements the app shell, settings, process list, Details, Task Manager-style Performance tiles/detail graphs, Services, Startup Apps, and an explicit Users stub. The parity target is 1:1 with Windows 11 Task Manager, but unsupported or incomplete items stay documented in [PARITY.md](PARITY.md).

## Build

```powershell
dotnet build FastTaskMgr.sln
```

## Diagnostic Check

```powershell
dotnet run --project src/FastTaskMgr/FastTaskMgr.csproj -- --self-check
```

## Publish

Main distribution is framework-dependent single-file `win-x64`:

```powershell
dotnet publish src/FastTaskMgr/FastTaskMgr.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

The executable is emitted under:

```text
src/FastTaskMgr/bin/Release/net10.0-windows/win-x64/publish/FastTaskMgr.exe
```

`.\build.ps1` also creates `artifacts\FastTaskMgr-Setup.exe` using Inno Setup 6. GitHub Actions installs Inno for release builds; local setup packaging needs Inno installed.

## Native AOT

Native AOT is intentionally not required for the main build. WinForms, reflection-adjacent framework code, and native interop need separate verification before AOT can be trusted. An experimental profile is included at `Properties/PublishProfiles/NativeAot.Experimental.pubxml` for future testing only.

## Performance Goals

- Fast first paint before expensive data is loaded.
- Responsive UI with 500+ processes.
- Normal update idle CPU target under 1% on a typical machine.
- No WMI polling, PowerShell, `wmic`, `tasklist`, `sc`, or `net` in routine data paths.
- Coalesced UI updates with virtualized tables.

## Dependencies

No third-party NuGet packages are used. Native Windows APIs and .NET libraries are preferred so the app can ship as a clean framework-dependent single executable.

## Known Limitations

- Exact Windows Task Manager grouping, startup impact, per-process disk/network, and GPU data are not fully implemented yet.
- Many service/process actions can require elevation; access denied is expected on protected/elevated targets.
- `PublishTrimmed` is disabled because WinForms and native interop paths need trim-specific verification.
