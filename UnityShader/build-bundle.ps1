# Build the fog shader AssetBundle and install it into the mod folder.
# Usage: powershell -ExecutionPolicy Bypass -File build-bundle.ps1

param(
    [string]$Unity = "C:\Program Files\Unity 2022.3.62F2\Editor\Unity.exe",
    [string]$Project = (Join-Path $PSScriptRoot "UnityProject"),
    [string]$ModDir = "C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die\Mods\SilentFogClient"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Unity)) {
    Write-Host "Unity not found: $Unity"
    exit 1
}

$assetsDir = Join-Path $Project "Assets"
$editorDir = Join-Path $assetsDir "Editor"

if (-not (Test-Path $assetsDir)) {
    Write-Host "Creating Unity project..."
    & $Unity -createProject $Project -quit -batchmode -nographics -logFile (Join-Path $PSScriptRoot "create.log")
}

New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null
New-Item -ItemType Directory -Force -Path $editorDir | Out-Null

Copy-Item (Join-Path $PSScriptRoot "Assets\*.shader") $assetsDir -Force
Copy-Item (Join-Path $PSScriptRoot "Assets\Editor\SilentFogBundleBuilder.cs") $editorDir -Force

Write-Host "Building AssetBundle..."
& $Unity -batchmode -nographics -quit -projectPath $Project -executeMethod SilentFogBundleBuilder.BuildFromCommandLine -logFile (Join-Path $PSScriptRoot "build.log")

$bundle = Join-Path $Project "Build\silentfogfog"
if (-not (Test-Path $bundle)) {
    Write-Host "Bundle was not built, see build.log"
    exit 2
}

$target = Join-Path $ModDir "AssetBundles"
New-Item -ItemType Directory -Force -Path $target | Out-Null

$installed = Join-Path $target "silentfogfog"
try {
    Copy-Item $bundle $target -Force -ErrorAction Stop
} catch {
    Write-Host "COPY FAILED (game running? file locked): $($_.Exception.Message)"
    exit 3
}

if ((Get-FileHash $bundle).Hash -ne (Get-FileHash $installed).Hash) {
    Write-Host "COPY VERIFY FAILED: installed bundle differs from build"
    exit 4
}

$kb = [math]::Round((Get-Item $installed).Length / 1KB)
Write-Host "OK: $installed ($kb KB, hash verified)"
