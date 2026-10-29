Get-Process HdmiWake -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
Unregister-ScheduledTask -TaskName 'HdmiWake' -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'HdmiWake.lnk') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'HdmiWake') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "HdmiWake removed."
