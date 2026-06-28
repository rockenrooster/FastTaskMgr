param(
    [string]$SourceExe = (Join-Path $PSScriptRoot "FastTaskMgr.exe"),
    [string]$InstallDir = (Join-Path $env:ProgramFiles "FastTaskMgr"),
    [switch]$NoDesktopShortcut,
    [switch]$NoStartMenuShortcut
)

$ErrorActionPreference = "Stop"
$repoLatest = "https://api.github.com/repos/rockenrooster/FastTaskMgr/releases/latest"

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Quote-Arg([string]$Value) {
    '"' + $Value.Replace('"', '\"') + '"'
}

function Get-ReleaseExe {
    $downloadPath = Join-Path $env:TEMP "FastTaskMgr-install-$([guid]::NewGuid()).exe"
    $release = Invoke-RestMethod -Uri $repoLatest -Headers @{ "User-Agent" = "FastTaskMgr-Setup" }
    $asset = $release.assets | Where-Object { $_.name -eq "FastTaskMgr.exe" } | Select-Object -First 1
    if (!$asset) {
        throw "FastTaskMgr.exe was not found on the latest GitHub release."
    }

    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $downloadPath -Headers @{ "User-Agent" = "FastTaskMgr-Setup" }
    $downloadPath
}

function New-FastTaskMgrShortcut([string]$Path, [string]$Target) {
    $directory = Split-Path $Path -Parent
    New-Item -ItemType Directory -Path $directory -Force | Out-Null

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $Target
    $shortcut.WorkingDirectory = Split-Path $Target -Parent
    $shortcut.IconLocation = "$Target,0"
    $shortcut.Save()
}

if (!(Test-Admin)) {
    $args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Quote-Arg $PSCommandPath))
    if ($PSBoundParameters.ContainsKey("SourceExe")) {
        $args += @("-SourceExe", (Quote-Arg $SourceExe))
    }
    if ($PSBoundParameters.ContainsKey("InstallDir")) {
        $args += @("-InstallDir", (Quote-Arg $InstallDir))
    }
    if ($NoDesktopShortcut) {
        $args += "-NoDesktopShortcut"
    }
    if ($NoStartMenuShortcut) {
        $args += "-NoStartMenuShortcut"
    }

    Start-Process powershell.exe -Verb RunAs -ArgumentList $args
    exit
}

if (!(Test-Path $SourceExe)) {
    Write-Host "FastTaskMgr.exe not found beside setup. Downloading latest release..." -ForegroundColor Cyan
    $SourceExe = Get-ReleaseExe
}

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
$targetExe = Join-Path $InstallDir "FastTaskMgr.exe"

Get-Process FastTaskMgr -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -eq $targetExe } catch { $false }
} | Stop-Process -Force -ErrorAction SilentlyContinue

Copy-Item -LiteralPath $SourceExe -Destination $targetExe -Force

if (!$NoStartMenuShortcut) {
    $startMenuShortcut = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\FastTaskMgr\FastTaskMgr.lnk"
    New-FastTaskMgrShortcut $startMenuShortcut $targetExe
}

if (!$NoDesktopShortcut) {
    $desktopShortcut = Join-Path $env:PUBLIC "Desktop\FastTaskMgr.lnk"
    New-FastTaskMgrShortcut $desktopShortcut $targetExe
}

New-Item -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\FastTaskMgr.exe" -Force | Out-Null
Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\FastTaskMgr.exe" -Name "(default)" -Value $targetExe
Set-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\FastTaskMgr.exe" -Name "Path" -Value $InstallDir

Write-Host "Installed FastTaskMgr to $targetExe" -ForegroundColor Green
