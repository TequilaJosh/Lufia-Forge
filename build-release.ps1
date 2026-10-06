<#
.SYNOPSIS
    Builds the Lufia Forge installer (Setup.exe) and, optionally, publishes it as a GitHub release
    that installed copies will auto-update to.

.EXAMPLE
    ./build-release.ps1                 # build only -> out/releases/LufiaForge-win-Setup.exe
    ./build-release.ps1 -Publish        # build and upload a GitHub release (tag v<Version>)
    ./build-release.ps1 -Publish -Prerelease   # upload as a GitHub pre-release (beta); stable installs ignore it

.NOTES
    Requires the Velopack CLI:  dotnet tool install -g vpk
    Publishing uses your GitHub CLI login (gh auth token).
    Bump <Version> in LufiaForge/LufiaForge/LufiaForge.csproj before each release;
    installed copies only update to a higher version.
#>
param(
    [switch]$Publish,
    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$project = Join-Path $root 'LufiaForge/LufiaForge/LufiaForge.csproj'
$repoUrl = 'https://github.com/TequilaJosh/Lufia-Forge-Releases'   # release-only repo (installers + update feed)
$pubDir  = Join-Path $root 'out/publish'
$relDir  = Join-Path $root 'out/releases'

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $project" }
Write-Host "Building Lufia Forge $version" -ForegroundColor Yellow

# 1. Self-contained publish (users don't need .NET installed)
if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $pubDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# 2. Bundle BizHawk next to the exe (the Memory Monitor looks for BizHawk\EmuHawk.exe there)
#    Personal files stay out: saves, savestates (they contain the ROM image), trace logs, the local config.
robocopy (Join-Path $root 'BizHawk') (Join-Path $pubDir 'BizHawk') /E /NFL /NDL /NJH /NJS /NP `
         /XD SaveRAM State `
         /XF *.log *.State *.SaveRAM *.bak LufiaForge_Summary_*.txt LufiaForge_ScriptSummary*.txt config.ini | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Copying BizHawk failed' }
# ship the committed BizHawk config, not the one changed by local use
git -C $root show HEAD:BizHawk/config.ini | Set-Content -Encoding utf8 (Join-Path $pubDir 'BizHawk/config.ini')

# 3. Fetch the previous release so vpk can build a small delta update (ignored if none exists yet)
$token = $null
if ($Publish) { $token = (gh auth token).Trim() }
try { vpk download github --repoUrl $repoUrl --outputDir $relDir $(if ($token) { '--token'; $token }) }
catch { Write-Host 'No previous Velopack release found; building a full package only.' }

# 4. Package: Setup.exe, portable zip, update packages and the release feed
vpk pack --packId LufiaForge --packVersion $version --packDir $pubDir --mainExe LufiaForge.exe --runtime win-x64 `
         --packTitle 'Lufia Forge' --icon (Join-Path $root 'LufiaForge/LufiaForge/Assets/icon.ico') `
         --outputDir $relDir
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

Write-Host "Installer: $(Join-Path $relDir 'LufiaForge-win-Setup.exe')" -ForegroundColor Green

# 5. Upload as a GitHub release; installed copies pick it up on their next start
if ($Publish) {
    # Releases live in the release-only repo, so the tag goes on its default branch; the release notes
    # name the source commit that was built.
    $commit = (git -C $root rev-parse --short HEAD).Trim()
    $pre = @(); if ($Prerelease) { $pre = @('--pre') }
    vpk upload github --repoUrl $repoUrl --token $token --outputDir $relDir `
        --publish --releaseName "Lufia Forge $version" --tag "v$version" @pre
    if ($LASTEXITCODE -ne 0) { throw 'vpk upload failed' }
    gh release edit "v$version" --repo ($repoUrl -replace '^https://github.com/', '') `
        --notes "Lufia Forge $version, built from TequilaJosh/Lufia-Forge@$commit.`n`nInstall with LufiaForge-win-Setup.exe; installed copies update themselves."
    if ($LASTEXITCODE -ne 0) { throw 'Setting the release notes failed' }
    Write-Host "Published v$version to $repoUrl/releases" -ForegroundColor Green
}
