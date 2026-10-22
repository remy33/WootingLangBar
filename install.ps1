# Builds WootingLangBar into .\dist, starts it, and registers it to start at logon (Startup folder shortcut).
# Run from any PowerShell:  powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist"
$exe  = Join-Path $dist "WootingLangBar.exe"

# Ask a running instance to exit cleanly (it restores the keyboard first); kill it only if that fails.
if (Test-Path $exe) { & $exe --exit --quiet }
Get-Process WootingLangBar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Push-Location $root
try { dotnet publish -c Release -o $dist | Out-Null } finally { Pop-Location }
if (-not (Test-Path $exe)) { throw "publish failed: $exe not found" }

$startup = [Environment]::GetFolderPath("Startup")
$lnk = Join-Path $startup "WootingLangBar.lnk"
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = $dist
$sc.Description = "Wooting 80HE: show the Windows input language on the light bar"
$sc.Save()

Start-Process -FilePath $exe -WorkingDirectory $dist
Write-Host "Installed. Running now and at every logon via $lnk"
Write-Host "Config: $dist\config.json   Log: $env:LOCALAPPDATA\WootingLangBar\log.txt"
