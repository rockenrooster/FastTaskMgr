param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Message
)

$ErrorActionPreference = "Stop"

function RunGit {
    & git @args
    if ($LASTEXITCODE -ne 0) {
        throw "git $($args -join ' ') failed"
    }
}

function Get-VersionFromContent {
    param([string]$Content, [string]$Source)

    if ($Content -notmatch '<AssemblyVersion>(\d+)\.(\d+)\.(\d+)\.(\d+)</AssemblyVersion>') {
        throw "AssemblyVersion not found in $Source"
    }

    [version]"$($matches[1]).$($matches[2]).$($matches[3]).$($matches[4])"
}

function Get-CurrentVersion {
    $csprojPath = Join-Path $PSScriptRoot "src\FastTaskMgr\FastTaskMgr.csproj"
    Get-VersionFromContent (Get-Content $csprojPath -Raw) $csprojPath
}

function Set-ProjectVersion {
    $csprojPath = Join-Path $PSScriptRoot "src\FastTaskMgr\FastTaskMgr.csproj"
    $content = Get-Content $csprojPath -Raw
    $content = $content -replace '<AssemblyVersion>.*?</AssemblyVersion>', "<AssemblyVersion>$Version</AssemblyVersion>"
    $content = $content -replace '<FileVersion>.*?</FileVersion>', "<FileVersion>$Version</FileVersion>"
    Set-Content $csprojPath -Value $content -NoNewline
}

function Get-DefaultReleaseVersion {
    $current = Get-CurrentVersion
    $headContent = git show HEAD:src/FastTaskMgr/FastTaskMgr.csproj 2>$null
    if ($LASTEXITCODE -eq 0 -and $headContent) {
        $headVersion = Get-VersionFromContent ($headContent -join "`n") "HEAD:src/FastTaskMgr/FastTaskMgr.csproj"
        if ($current -gt $headVersion) {
            return $current.ToString()
        }
    }

    "$($current.Major).$($current.Minor).$($current.Build).$($current.Revision + 1)"
}

function Get-GeneratedCommitBody {
    $lines = git diff --cached --name-status
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect staged changes."
    }

    if (!$lines) {
        return "Automated release."
    }

    $items = foreach ($line in $lines) {
        $parts = $line -split "`t"
        $status = $parts[0]
        $path = $parts[-1]
        $verb = switch -Regex ($status) {
            '^A' { "Added"; break }
            '^D' { "Removed"; break }
            '^R' { "Renamed"; break }
            default { "Updated" }
        }
        "- $verb $path"
    }

    "Changes:`n" + ($items -join "`n")
}

function Update-ChangelogForRelease {
    param([string]$ReleaseVersion)

    $changelogPath = Join-Path $PSScriptRoot "CHANGELOG.md"
    if (!(Test-Path $changelogPath)) {
        return $false
    }

    $content = Get-Content $changelogPath -Raw
    $match = [regex]::Match($content, '(?ms)^## Unreleased\s*?\r?\n(.*?)(?=^## |\z)')
    if (!$match.Success) {
        return $false
    }

    $placeholder = '- Add upcoming user-visible changes here before tagging a release.'
    $entries = @(($match.Groups[1].Value -split '\r?\n') | Where-Object { $_.TrimEnd() -and $_.Trim() -ne $placeholder })
    if (!($entries | Where-Object { $_ -match '^\s*[-*] ' })) {
        return $false
    }

    $date = Get-Date -Format 'yyyy-MM-dd'
    $newBlock = "## Unreleased`n`n$placeholder`n`n## $ReleaseVersion - $date`n`n" + ($entries -join "`n") + "`n`n"
    $content = $content.Substring(0, $match.Index) + $newBlock + $content.Substring($match.Index + $match.Length)
    Set-Content $changelogPath -Value $content -NoNewline
    return $true
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-DefaultReleaseVersion
}

$tag = "v$Version"
Write-Host "Releasing $tag" -ForegroundColor Cyan

$branch = (git branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
    throw "Could not determine the current branch."
}

$origin = (git remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($origin)) {
    throw "No git remote named origin is configured."
}

$null = git rev-parse -q --verify "refs/tags/$tag" 2>$null
if ($LASTEXITCODE -eq 0) {
    throw "Local tag $tag already exists."
}

$remoteTag = git ls-remote --tags origin "refs/tags/$tag"
if ($LASTEXITCODE -ne 0) {
    throw "Could not check remote tags."
}
if (![string]::IsNullOrWhiteSpace($remoteTag)) {
    throw "Remote tag $tag already exists."
}

Set-ProjectVersion

if (Update-ChangelogForRelease $Version) {
    Write-Host "Promoted CHANGELOG.md Unreleased notes to $Version" -ForegroundColor Cyan
}
else {
    Write-Host "CHANGELOG.md has no Unreleased notes; GitHub release notes will be auto-generated." -ForegroundColor Yellow
}

RunGit add -A

git diff --cached --quiet
if ($LASTEXITCODE -eq 1) {
    if ([string]::IsNullOrWhiteSpace($Message)) {
        RunGit commit -m "Release $tag" -m (Get-GeneratedCommitBody)
    }
    else {
        RunGit commit -m $Message
    }
}
elseif ($LASTEXITCODE -ne 0) {
    throw "Could not inspect staged changes."
}

RunGit push -u origin $branch
RunGit tag $tag
RunGit push origin $tag

Write-Host "Pushed $branch and $tag. GitHub Actions will build and upload FastTaskMgr-Setup.exe." -ForegroundColor Green
