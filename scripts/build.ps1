# 编译 deskhand.exe（.NET Framework 4.0 csc，无需 SDK）。
# 用法：pwsh -NoProfile -File scripts/build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
$source = Join-Path $root 'helper\deskhand.cs'
$output = Join-Path $root 'helper\deskhand.exe'
& $csc /nologo /target:exe /platform:anycpu /optimize+ /out:"$output" /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll "$source"
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
Get-Item $output | Select-Object FullName, Length
