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

$csprojPath = Join-Path $PSScriptRoot "src\FastTaskMgr\FastTaskMgr.csproj"
$setupCsprojPath = Join-Path $PSScriptRoot "src\FastTaskMgr.Setup\FastTaskMgr.Setup.csproj"
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
$setupPayloadDir = Join-Path $PSScriptRoot "src\FastTaskMgr.Setup\Payload"
$setupPublishDir = Join-Path $PSScriptRoot "src\FastTaskMgr.Setup\obj\script-publish"
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $setupPublishDir -Recurse -Force -ErrorAction SilentlyContinue

RunDotnet clean $csprojPath -c Release
RunDotnet publish $csprojPath -c Release -r win-x64 --self-contained false -o $publishDir /p:PublishSingleFile=true /p:PublishReadyToRun=true

$publishExe = Join-Path $publishDir "FastTaskMgr.exe"
New-Item -ItemType Directory -Path $setupPayloadDir -Force | Out-Null
Copy-Item $publishExe (Join-Path $setupPayloadDir "FastTaskMgr.exe") -Force
RunDotnet publish $setupCsprojPath -c Release -r win-x64 --self-contained false -o $setupPublishDir /p:PublishSingleFile=true /p:PublishReadyToRun=true /p:AssemblyVersion=$newVersion /p:FileVersion=$newVersion

$artifactDir = Join-Path $PSScriptRoot "artifacts"
$artifactExe = Join-Path $artifactDir "FastTaskMgr.exe"
$artifactSha = Join-Path $artifactDir "FastTaskMgr.exe.sha256"
$setupExe = Join-Path $setupPublishDir "FastTaskMgr.Setup.exe"
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

Copy-Item $setupExe $setupArtifact -Force
$setupHash = (Get-FileHash $setupArtifact -Algorithm SHA256).Hash.ToLowerInvariant()
"$setupHash  FastTaskMgr-Setup.exe" | Set-Content $setupSha

Write-Host "Published $artifactExe ($newVersion)" -ForegroundColor Green
