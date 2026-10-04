<#
.SYNOPSIS
    Builds the Lufia Forge installer (Setup.exe) and, optionally, publishes it as a GitHub release
    that installed copies will auto-update to.

.EXAMPLE
    ./build-release.ps1                 # build only -> out/releases/LufiaForge-win-Setup.exe
    ./build-release.ps1 -Publish        # build and upload a GitHub release (tag v<Version>)

.NOTES
    Requires the Velopack CLI:  dotnet tool install -g vpk
    Publishing uses your GitHub CLI login (gh auth token).
    Bump <Version> in LufiaForge/LufiaForge/LufiaForge.csproj before each release;
    installed copies only update to a higher version.
#>
param(
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$project = Join-Path $root 'LufiaForge/LufiaForge/LufiaForge.csproj'
$repoUrl = 'https://github.com/TequilaJosh/Lufia-Forge'
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
robocopy (Join-Path $root 'BizHawk') (Join-Path $pubDir 'BizHawk') /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Copying BizHawk failed' }

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
    # Tag the commit that was built (vpk otherwise tags the repo's default branch).
    # Push your commits first so GitHub knows this commit.
    $commit = (git -C $root rev-parse HEAD).Trim()
    vpk upload github --repoUrl $repoUrl --token $token --outputDir $relDir `
        --publish --releaseName "Lufia Forge $version" --tag "v$version" --targetCommitish $commit
    if ($LASTEXITCODE -ne 0) { throw 'vpk upload failed' }
    Write-Host "Published v$version to $repoUrl/releases" -ForegroundColor Green
}
