# FastTaskMgr

<p align="center">
  <img src="src/FastTaskMgr/Assets/FastTaskMgr.png" alt="FastTaskMgr logo" width="96">
</p>

FastTaskMgr is a fast, lightweight Windows 11 Task Manager alternative built with C#, .NET 10, and WinForms.

**Status:** early public build. The app is usable, but the parity target is Windows 11 Task Manager and some pages are still partial.

## Features

- Processes page with search, sorting, CPU, memory, and process I/O columns.
- Performance page with CPU, memory, disk, and network tiles and graphs.
- Details page with process metadata, priority, affinity display, handles, and threads.
- Services page backed by Service Control Manager APIs.
- Startup apps page for registry and startup-folder entries.
- Settings for theme, update speed, always-on-top, admin startup, and end-task confirmation.
- Signed update checks against GitHub Releases.

## Download

Get the latest build from [GitHub Releases](https://github.com/rockenrooster/FastTaskMgr/releases).

Recommended asset:

- `FastTaskMgr-Setup.exe` - installer, requires admin.

Portable asset:

- `FastTaskMgr.exe` - framework-dependent single-file executable.

FastTaskMgr targets Windows x64 and requires the .NET 10 Desktop Runtime when using framework-dependent builds.

## Build From Source

Requirements:

- Windows x64
- .NET 10 SDK
- Inno Setup 6, only if building the installer locally

Build:

```powershell
dotnet build FastTaskMgr.sln
```

Run:

```powershell
dotnet run --project src/FastTaskMgr/FastTaskMgr.csproj
```

Run the diagnostic self-check:

```powershell
dotnet run --project src/FastTaskMgr/FastTaskMgr.csproj -- --self-check
```

Create local release artifacts without incrementing the project version:

```powershell
.\build.ps1 -NoIncrement
```

Artifacts are written to `artifacts\`.

## Release Notes For Maintainers

Release tags use `vX.Y.Z.W`.

```powershell
.\release.ps1 -Version 0.1.0.30
```

Tagged releases are built by GitHub Actions. Signed update manifests require the `FASTTASKMGR_UPDATE_SIGNING_PRIVATE_KEY_PEM` secret.

## Known Gaps

- Users page is currently a stub.
- Exact Windows Task Manager grouping is not implemented.
- GPU, per-process network, startup impact, dump creation, wait-chain analysis, and full affinity editing are not implemented yet.
- Some process and service actions require elevation. Access denied is expected for protected or elevated targets.

See [PARITY.md](src/FastTaskMgr/PARITY.md) for the detailed parity matrix.

## Privacy

FastTaskMgr uses native Windows APIs and .NET libraries for routine data collection. Network access is limited to GitHub release update checks and user-triggered "search online" actions.

## License

No license file is currently included.
