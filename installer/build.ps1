# Builds the release artifacts: Native AOT publish, the installer and the portable zip.
#   pwsh installer/build.ps1            -> artifacts/installer/Comet-<version>-setup.exe, Comet-<version>-portable.zip
#   pwsh installer/build.ps1 -NoPublish -> package an existing artifacts/publish
param([switch]$NoPublish, [string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# Single version source: Directory.Build.props.
if (-not $Version) { $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
"Comet $Version"

$publish = Join-Path $root 'artifacts/publish'
if (-not $NoPublish) {
  # Native AOT needs vswhere (Visual Studio Installer) on PATH to find the C++ linker.
  $vsInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
  if (Test-Path $vsInstaller) { $env:PATH += ";$vsInstaller" }
  if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
  dotnet publish src/Comet.App/Comet.App.csproj -c Release -o $publish -p:Version=$Version
  if ($LASTEXITCODE -ne 0) { throw "publish failed" }
}

$out = Join-Path $root 'artifacts/installer'
New-Item -ItemType Directory $out -Force | Out-Null

# Portable zip: the same files, without debug symbols (published separately).
$zip = Join-Path $out "Comet-$Version-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
$stage = Join-Path $root 'artifacts/portable/Comet'
if (Test-Path (Split-Path $stage)) { Remove-Item (Split-Path $stage) -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item "$publish/*" $stage -Recurse -Exclude '*.pdb'
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
Compress-Archive -Path "$publish/*.pdb" -DestinationPath (Join-Path $out "Comet-$Version-symbols.zip") -Force

# Installer.
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
& $iscc /Q "/DAppVersion=$Version" "/DSourceDir=$publish" installer/Comet.iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem $out | ForEach-Object { "{0,-40} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
