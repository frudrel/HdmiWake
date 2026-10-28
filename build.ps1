$ErrorActionPreference = 'Stop'
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /nologo /target:winexe /platform:x64 /optimize+ `
    /out:"$PSScriptRoot\HdmiWake.exe" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "$PSScriptRoot\HdmiWake.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $PSScriptRoot\HdmiWake.exe"
