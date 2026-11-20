# 编译 实时硬件监控.exe
# 只依赖 Windows 自带的 .NET Framework 4.8（csc.exe），不需要安装 .NET SDK。
#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "找不到 csc.exe: $csc" }

$src = Join-Path $root 'src\MonitorApp.cs'
$html = Join-Path $root 'src\index.html'
$ico = Join-Path $root 'src\app.ico'
$distDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $distDir | Out-Null
$out = Join-Path $distDir '实时硬件监控.exe'

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 `
    "/win32icon:$ico" `
    "/resource:$html,SysMonitor.index.html" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll `
    "/out:$out" "$src"

if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }
Get-Item $out | Select-Object FullName, Length, LastWriteTime | Format-List
