# Builds the release artifacts for one architecture: Native AOT publish, the installer and the portable zip.
#   pwsh installer/build.ps1 -Version 1.2.3 [-Arch x64|arm64] [-NoPublish]
#   -> artifacts/installer/Comet-Setup-<arch>.exe, Comet-Portable-<arch>.zip, Comet-Symbols-<arch>.zip
# Asset names carry no version, so links to releases/latest/download/<name> never change.
param([switch]$NoPublish, [string]$Version = '0.0.0', [ValidateSet('x64', 'arm64')][string]$Arch = 'x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
"Comet $Version ($Arch)"

$publish = Join-Path $root "artifacts/publish-$Arch"
if (-not $NoPublish) {
  # Native AOT needs vswhere (Visual Studio Installer) on PATH to find the C++ linker.
  $vsInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
  if (Test-Path $vsInstaller) { $env:PATH += ";$vsInstaller" }
  if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
  dotnet publish src/Comet.App/Comet.App.csproj -c Release -r "win-$Arch" -p:Platform=$(if ($Arch -eq 'arm64') { 'ARM64' } else { 'x64' }) -o $publish -p:Version=$Version
  if ($LASTEXITCODE -ne 0) { throw "publish failed" }
}

$out = Join-Path $root 'artifacts/installer'
New-Item -ItemType Directory $out -Force | Out-Null

# Portable zip: the same files without debug symbols, plus the "portable" marker (data stays in Data\ next to Comet.exe).
$zip = Join-Path $out "Comet-Portable-$Arch.zip"
if (Test-Path $zip) { Remove-Item $zip }
$stage = Join-Path $root "artifacts/portable-$Arch/Comet"
if (Test-Path (Split-Path $stage)) { Remove-Item (Split-Path $stage) -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item "$publish/*" $stage -Recurse -Exclude '*.pdb'
Set-Content -Path (Join-Path $stage 'portable') -Value 'Comet keeps its settings in the Data folder next to Comet.exe while this file is here.'
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
Compress-Archive -Path "$publish/*.pdb" -DestinationPath (Join-Path $out "Comet-Symbols-$Arch.zip") -Force

# Installer (per-user, Inno Setup).
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$publish" "/DArch=$Arch" installer/Comet.iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem $out | ForEach-Object { "{0,-32} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
