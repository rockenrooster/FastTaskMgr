# FastTaskMgr

<p align="center">
  <img src="src/FastTaskMgr/Assets/FastTaskMgr.png" alt="FastTaskMgr logo" width="96">
</p>

FastTaskMgr is a fast, lightweight Windows 11 Task Manager alternative built with C#, .NET 10, and WinForms.

**Status:** early public build. The app is usable, but the parity target is Windows 11 Task Manager and some pages are still partial.

## Features

- Processes page with search, sorting, CPU, memory, and process I/O columns, plus end task, end process tree, and go to details.
- Fast process sampling: one system snapshot call per refresh instead of per-process handle queries.
- Performance page with CPU, memory, disk, network, and GPU tiles and graphs, including the live CPU clock, GPU and SSD temperature, and SSD remaining life.
- Details page with user, architecture, description, company, command line, priority, and affinity, including set priority, set affinity, and end process tree.
- Users page with per-session process, CPU, and memory rollup plus disconnect and sign off.
- Services page backed by Service Control Manager APIs, with group column and go to details.
- Startup apps page for registry and startup-folder entries.
- Settings for theme, update speed, always-on-top, admin startup, and end-task confirmation.
- Tray icon restore when hide-on-minimize is enabled.
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
Add user-visible changes to the `Unreleased` section of [CHANGELOG.md](CHANGELOG.md) as you go. `release.ps1` promotes those notes to a versioned section, and the release workflow publishes them as the GitHub release notes. If no notes are present, GitHub's auto-generated notes are used instead.

```powershell
.\release.ps1 -Version 0.1.0.30
```

Tagged releases are built by GitHub Actions. Signed update manifests require the `FASTTASKMGR_UPDATE_SIGNING_PRIVATE_KEY_PEM` secret.

## Known Gaps

- Exact Windows Task Manager grouping is not implemented.
- GPU per-process usage, per-process network, startup impact, dump creation, and wait-chain analysis are not implemented yet.
- Some process and service actions require elevation. Access denied is expected for protected or elevated targets.

See [PARITY.md](src/FastTaskMgr/PARITY.md) for the detailed parity matrix.

## Privacy

FastTaskMgr uses native Windows APIs and .NET libraries for routine data collection. Network access is limited to GitHub release update checks and user-triggered "search online" actions.

## License

No license file is currently included.
