# Builds WootingLangBar into .\dist, starts it, and registers it to start at logon (Startup folder shortcut).
# Run from any PowerShell:  powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist"
$exe  = Join-Path $dist "WootingLangBar.exe"
$cfg  = Join-Path $dist "config.json"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet not found: install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0"
}

# Ask a running instance to exit cleanly (it restores the keyboard first); kill it only if that fails.
# -Wait matters: PowerShell does not wait for a GUI exe started with "&", so the kill below would cut the clean exit short.
if (Test-Path $exe) { Start-Process -FilePath $exe -ArgumentList "--exit", "--quiet" -Wait }
Get-Process WootingLangBar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Publish overwrites dist\config.json whenever the repo copy is newer (a git pull makes it newer): keep the user's copy.
$userCfg = if (Test-Path $cfg) { [IO.File]::ReadAllBytes($cfg) } else { $null }
Push-Location $root
try { $out = dotnet publish -c Release -o $dist; $code = $LASTEXITCODE }
finally {
    Pop-Location
    if ($null -ne $userCfg) { [IO.File]::WriteAllBytes($cfg, $userCfg) }
}
if ($code -ne 0) {
    $out | Write-Host
    $note = ""
    if (Test-Path $exe) { Start-Process -FilePath $exe -WorkingDirectory $dist; $note = " The previous version was restarted." }
    throw "Build failed (dotnet publish exit code $code), see the output above.$note"
}

$startup = [Environment]::GetFolderPath("Startup")
$lnk = Join-Path $startup "WootingLangBar.lnk"
$ws = New-Object -ComObject WScript.Shell
$sc = $ws.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.WorkingDirectory = $dist
$sc.Description = "Wooting 80HE: show the Windows input language on the light bar or keys"
$sc.Save()

Start-Process -FilePath $exe -WorkingDirectory $dist
Write-Host "Installed. Running now and at every logon via $lnk"
Write-Host "Config: $dist\config.json   Log: $env:LOCALAPPDATA\WootingLangBar\log.txt"
