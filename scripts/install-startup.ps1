# 编译 helper、注册登录启动项、并立即启动 watcher。
# 用法：双击 install-startup.cmd，或
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-startup.ps1
# 说明：helper 必须由 Medium 进程编译并抬到 Medium 完整性标签，
#      否则 Windows 会按文件标签把 broker 限制在 Low，导致无法注入输入。
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'helper\deskhand.exe'
$src = Join-Path $root 'helper\deskhand.cs'
$log = Join-Path $root 'data\watch.log'
if (-not (Test-Path (Join-Path $root 'data'))) { New-Item -ItemType Directory -Path (Join-Path $root 'data') | Out-Null }

$groups = whoami /groups 2>$null | Out-String
$integrity = 'Unknown'
if ($groups -match 'S-1-16-4096') { $integrity = 'Low' }
elseif ($groups -match 'S-1-16-8192') { $integrity = 'Medium' }
elseif ($groups -match 'S-1-16-12288') { $integrity = 'High' }
"installer integrity: $integrity"
Add-Content -Path $log -Value ("installer run integrity=" + $integrity + " pid=" + $PID)

Get-Process deskhand -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (Test-Path $csc) {
    $uiaClient = (Get-ChildItem 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient' -Recurse -Filter 'UIAutomationClient.dll' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
    $uiaTypes = (Get-ChildItem 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes' -Recurse -Filter 'UIAutomationTypes.dll' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
    $windowsBase = (Get-ChildItem 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\WindowsBase' -Recurse -Filter 'WindowsBase.dll' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
    $speech = (Get-ChildItem 'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech' -Recurse -Filter 'System.Speech.dll' -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
    if (-not $uiaClient -or -not $uiaTypes -or -not $windowsBase) { throw "UIAutomation assemblies not found" }
    if (-not $speech) { throw "System.Speech assembly not found" }
    $buildExe = Join-Path $root 'helper\deskhand.build.exe'
    & $csc /nologo /target:exe /platform:anycpu /optimize+ /out:"$buildExe" /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"$uiaClient" /r:"$uiaTypes" /r:"$windowsBase" /r:"$speech" "$src" | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $buildExe)) { throw "csc build failed (exit $LASTEXITCODE)" }
    Move-Item -Force $buildExe $exe
    "build: ok"
} else {
    "csc not found: keep existing helper.exe"
}

$relabel = icacls $exe /setintegritylevel Medium 2>&1
"relabel: $relabel"
$label = (icacls $exe 2>$null | Select-String 'Mandatory Label').Line
if ($label) { "label now: $label" }

$startup = [Environment]::GetFolderPath('Startup')
$link = Join-Path $startup 'deskhand-watch.lnk'
$old = Join-Path $startup 'deskhand-watch.cmd'
if (Test-Path $old) { Remove-Item $old -Force }

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($link)
$shortcut.TargetPath = $exe
$shortcut.Arguments = 'watch'
$shortcut.WorkingDirectory = Split-Path -Parent $exe
$shortcut.WindowStyle = 7
$shortcut.Save()
"installed: $link"

$before = 0
if (Test-Path $log) { $before = @(Get-Content $log).Count }
Start-Process -FilePath $exe -ArgumentList 'watch' -WindowStyle Hidden

Start-Sleep -Seconds 3
$running = Get-Process deskhand -ErrorAction SilentlyContinue
if ($running) {
    $ids = ($running | ForEach-Object { $_.Id }) -join ','
    "watcher running: $ids"
} else {
    "watcher NOT running"
}
if (Test-Path $log) {
    "---- watch.log (new lines) ----"
    $lines = @(Get-Content $log)
    $newLines = $lines | Select-Object -Skip $before
    $newLines | ForEach-Object { $_ }
    $lastBroker = $newLines | Select-String 'broker serve' | Select-Object -Last 1
    if ($lastBroker -and $lastBroker.Line -match 'integrity=S-1-16-4096') {
        ""
        "警告：这次 broker 是 Low 完整性，无法注入输入。"
        "如果 label now 不是 Medium，说明 relabel 失败；需要管理员权限处理文件标签。"
    }
} else {
    "no watch.log yet"
}