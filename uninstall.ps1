# Stops WootingLangBar and removes the logon shortcut. Keeps the built files and the log.
$ErrorActionPreference = "SilentlyContinue"
$exe = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "dist\WootingLangBar.exe"
# Clean exit restores the keyboard; -Wait because PowerShell does not wait for a GUI exe. Kill only if that fails.
if (Test-Path $exe) { Start-Process -FilePath $exe -ArgumentList "--exit", "--quiet" -Wait }
Get-Process WootingLangBar | Stop-Process -Force
$lnk = Join-Path ([Environment]::GetFolderPath("Startup")) "WootingLangBar.lnk"
if (Test-Path $lnk) { Remove-Item $lnk; Write-Host "Removed $lnk" }
Write-Host "WootingLangBar stopped and unregistered from startup."
