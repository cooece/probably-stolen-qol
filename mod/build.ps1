# 一键编译 + 部署到游戏 Mods 目录
# 用法：右键「使用 PowerShell 运行」，或在终端里执行
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
# 游戏装在别处时：
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -GameDir "D:\Steam\steamapps\common\Probably Stolen"

param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Probably Stolen",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = Join-Path $root "..\.tools\dotnet\dotnet.exe"

if (-not (Test-Path $dotnet)) {
    Write-Host "找不到 dotnet：$dotnet" -ForegroundColor Red
    Write-Host "需要先安装 .NET SDK 8，或修改本脚本里的路径。" -ForegroundColor Red
    exit 1
}

Write-Host "编译中…" -ForegroundColor Cyan
& $dotnet build (Join-Path $root "QoL\QoL.csproj") -c $Configuration -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败，看上面的报错。" -ForegroundColor Red
    exit $LASTEXITCODE
}

$dest = Join-Path $GameDir "Mods\qol"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

try {
    Copy-Item (Join-Path $root "QoL\bin\$Configuration\QoL.dll") $dest -Force
    Copy-Item (Join-Path $root "QoL\manifest.xml") $dest -Force
}
catch {
    Write-Host ""
    Write-Host "部署失败：游戏正在运行，DLL 被占用，没法覆盖。" -ForegroundColor Yellow
    Write-Host "请完全退出游戏（任务管理器里确认 Probably Stolen 已关闭）后重跑本脚本。" -ForegroundColor Yellow
    exit 2
}

Write-Host "已部署到：$dest" -ForegroundColor Green
Write-Host "启动游戏后到「模组」页面确认它出现在列表里。" -ForegroundColor Green
