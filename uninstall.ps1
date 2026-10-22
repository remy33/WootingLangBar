# Stops WootingLangBar and removes the logon shortcut. Keeps the built files and the log.
$ErrorActionPreference = "SilentlyContinue"
$exe = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "dist\WootingLangBar.exe"
if (Test-Path $exe) { & $exe --exit --quiet }     # clean exit restores the keyboard; kill only if that fails
Get-Process WootingLangBar | Stop-Process -Force
$lnk = Join-Path ([Environment]::GetFolderPath("Startup")) "WootingLangBar.lnk"
if (Test-Path $lnk) { Remove-Item $lnk; Write-Host "Removed $lnk" }
Write-Host "WootingLangBar stopped and unregistered from startup."
