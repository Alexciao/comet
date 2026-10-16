# Install -> update -> uninstall test for the per-user installer (no admin rights needed).
#   pwsh installer/test-install.ps1 -Old <Comet-1.0.0-setup.exe> -New <Comet-1.0.1-setup.exe>
# Uses the real per-user locations, so it refuses to run if Comet is already installed or running, and it
# removes the data folder afterwards only if this test created it.
param([Parameter(Mandatory)] [string]$Old, [Parameter(Mandatory)] [string]$New)
$ErrorActionPreference = 'Stop'

$app = Join-Path $env:LOCALAPPDATA 'Programs\Comet'
$exe = Join-Path $app 'Comet.exe'
$data = Join-Path $env:LOCALAPPDATA 'Comet'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Comet.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B4E7A1C9-3D52-4F6E-9A1B-7C2D8E0F4A65}_is1'

if (Test-Path $app) { throw "Comet is already installed in $app; not touching it." }
if (Get-Process Comet -ErrorAction SilentlyContinue) { throw 'A Comet process is running; quit it first.' }
$dataExisted = Test-Path $data
$failures = 0

function Check([string]$what, [bool]$ok) {
  if ($ok) { "  ok    $what" } else { "  FAIL  $what"; $script:failures++ }
}

function RunKey { (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).Comet }

function Comet { Get-Process Comet -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } }

function WaitFor([scriptblock]$condition, [int]$seconds = 15) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while (-not (& $condition)) { if ((Get-Date) -gt $deadline) { return $false }; Start-Sleep -Milliseconds 200 }
  return $true
}

# Start-Process -Wait would also wait for Comet, which setup starts: wait for the setup process alone.
function Setup([string]$installer) {
  $p = Start-Process $installer '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -PassThru
  $p.WaitForExit()
  return $p.ExitCode
}

"Install $(Split-Path $Old -Leaf)"
Check 'setup exit code 0' ((Setup $Old) -eq 0)
Check 'Comet.exe installed' (Test-Path $exe)
Check 'no debug symbols or XML docs shipped' (-not (Get-ChildItem $app -Include *.pdb, *.xml -Recurse))
Check 'Start menu shortcut' (Test-Path $shortcut)
Check 'Comet started in the tray' (WaitFor { Comet })
Check 'start-at-login entry points at the installed exe' (WaitFor { (RunKey) -eq "`"$exe`" --background" })
Check 'uninstall entry has version 1.0.0' ((Get-ItemProperty $uninstallKey).DisplayVersion -eq '1.0.0')
$firstPid = (Comet).Id
Check 'log shows 1.0.0 started' (WaitFor { (Get-Content "$data\logs\comet.log" -Raw) -match 'Comet 1\.0\.0 starting' })

"Update to $(Split-Path $New -Leaf) while 1.0.0 is running"
Check 'setup exit code 0' ((Setup $New) -eq 0)
Check '1.0.0 quit cleanly (its log says stopped)' ((Get-Content "$data\logs\comet.log" -Raw) -match 'Comet stopped')
Check 'old process gone' (-not (Get-Process -Id $firstPid -ErrorAction SilentlyContinue))
Check 'Comet running again' (WaitFor { Comet })
Check 'log shows 1.0.1 started' (WaitFor { (Get-Content "$data\logs\comet.log" -Raw) -match 'Comet 1\.0\.1 starting' })
Check 'uninstall entry has version 1.0.1' ((Get-ItemProperty $uninstallKey).DisplayVersion -eq '1.0.1')
Check 'file version is 1.0.1' ((Get-Item $exe).VersionInfo.ProductVersion -like '1.0.1*')
Check 'user data folder survived the update' (Test-Path "$data\logs\comet.log")

"Uninstall"
$uninstaller = (Get-ItemProperty $uninstallKey).UninstallString.Trim('"')
Check 'uninstaller exit code 0' ((Setup $uninstaller) -eq 0)
Check 'Comet no longer running' (WaitFor { -not (Comet) })
Check 'program folder removed' (WaitFor { -not (Test-Path $app) })
Check 'Start menu shortcut removed' (-not (Test-Path $shortcut))
Check 'start-at-login entry removed' (-not (RunKey))
Check 'uninstall entry removed' (-not (Test-Path $uninstallKey))
Check 'user data kept' (Test-Path "$data\logs\comet.log")
Check 'clean quit logged' ((Select-String -Path "$data\logs\comet.log" -Pattern 'Comet stopped').Count -ge 2)

if (-not $dataExisted) { Remove-Item $data -Recurse -Force }
if ($failures) { "$failures check(s) failed"; exit 1 } else { 'all checks passed' }
