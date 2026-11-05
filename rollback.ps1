<#
    查看版本 / 回退到某个版本

    用法：
        .\rollback.ps1                      列出现有版本
        .\rollback.ps1 v2026.11.05-2227     回退到这一版

    回退会丢弃当前未提交的改动，脚本会先问一次。
#>
param([Parameter(Position = 0)][string]$Version = "")

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if ([string]::IsNullOrWhiteSpace($Version)) {
    Write-Host "现有版本（新的在上）：" -ForegroundColor Cyan
    Write-Host ""
    git tag -l --sort=-creatordate | ForEach-Object {
        $desc = git log -1 --format="%s" $_
        Write-Host ("  {0,-22} {1}" -f $_, $desc)
    }
    Write-Host ""
    Write-Host "回退：.\rollback.ps1 <版本号>" -ForegroundColor DarkGray
    exit 0
}

if (-not (git tag -l $Version)) {
    Write-Host "找不到版本 $Version" -ForegroundColor Red
    Write-Host "运行 .\rollback.ps1 不带参数可以看全部版本。" -ForegroundColor DarkGray
    exit 1
}

$dirty = git status --porcelain
if ($dirty) {
    Write-Host "当前有未提交的改动，回退会丢弃它们：" -ForegroundColor Yellow
    git status --short | Out-Host
    $ans = Read-Host "继续？(y/N)"
    if ($ans -ne 'y') {
        Write-Host "已取消。"
        exit 0
    }
}

git reset --hard $Version

Write-Host ""
Write-Host "已回退到 $Version，重新编译…" -ForegroundColor Cyan
$game = "C:\Program Files (x86)\Steam\steamapps\common\Probably Stolen"
& "$root\.tools\dotnet\dotnet.exe" build "mod\QoL\QoL.csproj" -c Release "-p:GameDir=$game" | Out-Host

if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败，但源码已经回退。" -ForegroundColor Red
    exit 2
}

Write-Host ""
Write-Host "源码已回退并编译完成。" -ForegroundColor Green
Write-Host "部署到游戏：mod\build.ps1" -ForegroundColor DarkGray
