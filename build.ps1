param(
    [string]$Version,
    [switch]$NoIncrement
)

$ErrorActionPreference = "Stop"

function RunDotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args -join ' ') failed"
    }
}

function Get-InnoCompiler {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidate = Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"
    if (Test-Path $candidate) {
        return $candidate
    }

    return $null
}

$csprojPath = Join-Path $PSScriptRoot "src\FastTaskMgr\FastTaskMgr.csproj"
$innoScript = Join-Path $PSScriptRoot "installer\FastTaskMgr.iss"
$content = Get-Content $csprojPath -Raw

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "Version must be x.y.z.w"
    }
    $newVersion = $Version
}
elseif ($content -match '<AssemblyVersion>(\d+)\.(\d+)\.(\d+)\.(\d+)</AssemblyVersion>') {
    if ($NoIncrement) {
        $newVersion = "$($matches[1]).$($matches[2]).$($matches[3]).$($matches[4])"
    }
    else {
        $newVersion = "$($matches[1]).$($matches[2]).$($matches[3]).$([int]$matches[4] + 1)"
    }
}
else {
    throw "AssemblyVersion not found in $csprojPath"
}

$content = $content -replace '<AssemblyVersion>.*?</AssemblyVersion>', "<AssemblyVersion>$newVersion</AssemblyVersion>"
$content = $content -replace '<FileVersion>.*?</FileVersion>', "<FileVersion>$newVersion</FileVersion>"
Set-Content $csprojPath -Value $content -NoNewline

$publishDir = Join-Path $PSScriptRoot "src\FastTaskMgr\obj\script-publish"
$artifactDir = Join-Path $PSScriptRoot "artifacts"
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue

RunDotnet clean $csprojPath -c Release
RunDotnet publish $csprojPath -c Release -r win-x64 --self-contained false -o $publishDir /p:PublishSingleFile=true /p:PublishReadyToRun=true

$publishExe = Join-Path $publishDir "FastTaskMgr.exe"
$artifactExe = Join-Path $artifactDir "FastTaskMgr.exe"
$artifactSha = Join-Path $artifactDir "FastTaskMgr.exe.sha256"
$setupArtifact = Join-Path $artifactDir "FastTaskMgr-Setup.exe"
$setupSha = Join-Path $artifactDir "FastTaskMgr-Setup.exe.sha256"

New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null
Remove-Item (Join-Path $artifactDir "FastTaskMgr-Setup.ps1") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $artifactDir "FastTaskMgr-Setup.ps1.sha256") -Force -ErrorAction SilentlyContinue
try {
    Copy-Item $publishExe $artifactExe -Force -ErrorAction Stop
}
catch {
    throw "Could not replace $artifactExe. Close any running FastTaskMgr.exe from artifacts, then retry."
}

$hash = (Get-FileHash $artifactExe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  FastTaskMgr.exe" | Set-Content $artifactSha

$iscc = Get-InnoCompiler
if ($iscc) {
    & $iscc "/DAppVersion=$newVersion" "/DSourceDir=$publishDir" "/DOutputDir=$artifactDir" $innoScript
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC failed"
    }

    $setupHash = (Get-FileHash $setupArtifact -Algorithm SHA256).Hash.ToLowerInvariant()
    "$setupHash  FastTaskMgr-Setup.exe" | Set-Content $setupSha
}
elseif ($env:GITHUB_ACTIONS -eq "true") {
    throw "Inno Setup 6 is required to build FastTaskMgr-Setup.exe."
}
else {
    Write-Warning "Inno Setup 6 not found; skipped FastTaskMgr-Setup.exe."
    Remove-Item $setupArtifact, $setupSha -Force -ErrorAction SilentlyContinue
}

Write-Host "Published $artifactExe ($newVersion)" -ForegroundColor Green
