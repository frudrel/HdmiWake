# Copies HdmiWake.exe to %LOCALAPPDATA%\HdmiWake, registers a Task Scheduler task that starts it
# at logon, and starts it. Run from your own PowerShell (not from inside a packaged app such as
# Claude, whose AppData writes are redirected to a private folder).
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'HdmiWake'
$exe = Join-Path $dir 'HdmiWake.exe'
$taskName = 'HdmiWake'
New-Item -ItemType Directory -Force $dir | Out-Null

Get-Process HdmiWake -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
Copy-Item "$PSScriptRoot\HdmiWake.exe" $exe -Force

# A logon task starts right at sign-in; the Startup folder runs 20+ seconds later, after other apps.
$user = "$env:USERDOMAIN\$env:USERNAME"
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $dir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -Priority 4
$lnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'HdmiWake.lnk'
try
{
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description 'Brings external monitors back after logon and resume.' -Force | Out-Null
    Remove-Item $lnk -ErrorAction SilentlyContinue
    Start-ScheduledTask -TaskName $taskName
    Write-Host "Installed to $exe; starts at logon via Task Scheduler ($taskName)."
}
catch
{
    # Fall back to the Startup folder if the task can't be created.
    $sh = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $sh.TargetPath = $exe
    $sh.WorkingDirectory = $dir
    $sh.Save()
    Start-Process $exe
    Write-Host "Installed to $exe; starts at logon via the Startup folder (task failed: $($_.Exception.Message))."
}
