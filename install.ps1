# Copies HdmiWake.exe to %LOCALAPPDATA%\HdmiWake, adds it to Startup (runs at every logon) and starts it.
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'HdmiWake'
$exe = Join-Path $dir 'HdmiWake.exe'
New-Item -ItemType Directory -Force $dir | Out-Null

Get-Process HdmiWake -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
Copy-Item "$PSScriptRoot\HdmiWake.exe" $exe -Force

$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'HdmiWake.lnk'
$sh = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$sh.TargetPath = $exe
$sh.WorkingDirectory = $dir
$sh.Save()

Start-Process $exe
Write-Host "Installed to $exe and added to Startup."
