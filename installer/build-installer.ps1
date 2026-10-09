# Copyright (c) 2026 Rausch IT
# SPDX-License-Identifier: MIT

# build-installer.ps1
# Publishes DisplayProfiles (tray app + CLI) and compiles the Inno Setup installer.
# Uses the Tools.InnoSetup NuGet package — no system installation required (same approach as SimFeedback).
#
# Usage:
#   .\build-installer.ps1                 Publish + installer -> Releases\<version>\DisplayProfiles-<version>-setup.exe
#   .\build-installer.ps1 -NoInstaller    Publish only (build\publish)
#
# Version: VERSION file in the repository root (also used by all projects via Directory.Build.props).

param(
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'

$ProjectRoot = Split-Path $PSScriptRoot -Parent
$IssFile     = Join-Path $PSScriptRoot 'displayprofiles.iss'
$Version     = (Get-Content (Join-Path $ProjectRoot 'VERSION')).Trim()
$PublishDir  = Join-Path $ProjectRoot 'build\publish'
$OutputDir   = Join-Path (Join-Path $ProjectRoot 'Releases') $Version
$TrayProj    = Join-Path $ProjectRoot 'src\DisplayProfiles.Tray\DisplayProfiles.Tray.csproj'
$CliProj     = Join-Path $ProjectRoot 'src\DisplayProfiles.Cli\DisplayProfiles.Cli.csproj'

$Steps = if ($NoInstaller) { 1 } else { 2 }

Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  DisplayProfiles Release Build"               -ForegroundColor Cyan
Write-Host "  Version: $Version"                    -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# --- Step 1: Publish ------------------------------------------------------
# Publish into a fresh folder per run; old publish folders are left in place (renamed), never deleted.
Write-Host "[1/$Steps] Publishing to $PublishDir ..." -ForegroundColor Yellow
if (Test-Path $PublishDir) {
    $old = "$PublishDir-old-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Rename-Item $PublishDir (Split-Path $old -Leaf)
    Write-Host "  Previous publish folder kept as $old" -ForegroundColor DarkGray
}
foreach ($proj in @($TrayProj, $CliProj)) {
    dotnet publish $proj -c Release -o $PublishDir --no-self-contained -nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: dotnet publish failed for $proj" -ForegroundColor Red
        exit 1
    }
}

if ($NoInstaller) {
    Write-Host ""
    Write-Host "Publish complete: $PublishDir" -ForegroundColor Green
    exit 0
}

# --- Resolve ISCC.exe from Tools.InnoSetup NuGet package ------------------
$NuGetPackage = 'Tools.InnoSetup'
$NuGetVersion = '6.7.1'
$ToolsDir     = Join-Path $ProjectRoot 'build\tools'
$IsccPath     = Join-Path $ToolsDir "tools.innosetup\$NuGetVersion\tools\ISCC.exe"

if (-not (Test-Path $IsccPath)) {
    Write-Host "  Restoring $NuGetPackage $NuGetVersion..." -ForegroundColor DarkGray
    New-Item -ItemType Directory -Path $ToolsDir -Force | Out-Null
    $TmpDir  = Join-Path $ProjectRoot 'build\innosetup-restore'
    New-Item -ItemType Directory -Path $TmpDir -Force | Out-Null
    $TmpProj = Join-Path $TmpDir 'innosetup-restore.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="$NuGetPackage" Version="$NuGetVersion" /></ItemGroup>
</Project>
"@ | Set-Content $TmpProj
    dotnet restore $TmpProj --packages $ToolsDir
}

if (-not (Test-Path $IsccPath)) {
    Write-Host "ERROR: Could not resolve ISCC.exe from NuGet package '$NuGetPackage'." -ForegroundColor Red
    exit 1
}

# --- Step 2: Compile Inno Setup installer ---------------------------------
Write-Host "[2/$Steps] Compiling installer..." -ForegroundColor Yellow
Write-Host "  Using:  $IsccPath" -ForegroundColor DarkGray
Write-Host "  Output: $OutputDir" -ForegroundColor DarkGray

$LogFile = Join-Path $ProjectRoot 'build\iscc-build.log'
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

& $IsccPath $IssFile "/O$OutputDir" "/DPublishDir=$PublishDir" 2>&1 | Tee-Object -FilePath $LogFile
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Inno Setup compilation failed with exit code $LASTEXITCODE (log: $LogFile)" -ForegroundColor Red
    exit 1
}

$InstallerFile = Get-ChildItem $OutputDir -Filter "DisplayProfiles-$Version-setup.exe" |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

Write-Host ""
Write-Host "Build complete!" -ForegroundColor Green
Write-Host "  Installer: $($InstallerFile.FullName)" -ForegroundColor Green
Write-Host "  Size:      $([math]::Round($InstallerFile.Length / 1MB, 1)) MB" -ForegroundColor Green
