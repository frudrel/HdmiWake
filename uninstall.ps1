Get-Process HdmiWake -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'HdmiWake.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'HdmiWake') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "HdmiWake removed."
