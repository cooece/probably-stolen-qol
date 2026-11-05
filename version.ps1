<#
    打一个版本点：编译 → 提交 → 打标签 → 记进 CHANGELOG

    用法：
        .\version.ps1 "新增：提示框配色可调"
        .\version.ps1 -SkipBuild "只改了文档"

    每打一次，就多一个能回去的点。
#>
param(
    [Parameter(Position = 0)][string]$Message = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if ([string]::IsNullOrWhiteSpace($Message)) {
    Write-Host "请给这次改动一句话说明，例如：" -ForegroundColor Yellow
    Write-Host '  .\version.ps1 "修复：标签圆角过圆"' -ForegroundColor DarkGray
    exit 1
}

$changes = git status --porcelain
if (-not $changes) {
    Write-Host "没有检测到改动，不创建版本。" -ForegroundColor Yellow
    exit 0
}

if (-not $SkipBuild) {
    Write-Host "编译中…" -ForegroundColor Cyan
    $game = "C:\Program Files (x86)\Steam\steamapps\common\Probably Stolen"
    & "$root\.tools\dotnet\dotnet.exe" build "mod\QoL\QoL.csproj" -c Release "-p:GameDir=$game" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "编译失败，版本未创建。" -ForegroundColor Red
        exit 2
    }
}

$tag = "v" + (Get-Date -Format "yyyy.MM.dd-HHmm")
$now = Get-Date -Format "MM-dd HH:mm"

$hash = ""
$dll = Join-Path $root "mod\QoL\bin\Release\QoL.dll"
if (Test-Path $dll) {
    $hash = (Get-FileHash $dll -Algorithm SHA256).Hash.Substring(0, 16)
}

git add -A
git commit -q -m $Message
git tag $tag

$log = Join-Path $root "CHANGELOG.md"
if (-not (Test-Path $log)) {
    "# 版本记录`r`n`r`n| 版本 | 时间 | 说明 | DLL |`r`n|---|---|---|---|`r`n" |
        Out-File $log -Encoding utf8
}

"| $tag | $now | $Message | $hash |" | Out-File $log -Encoding utf8 -Append

Write-Host ""
Write-Host "已创建版本 $tag" -ForegroundColor Green
Write-Host "  说明：$Message"
if ($hash) { Write-Host "  DLL ：$hash" }
Write-Host "  回退：.\rollback.ps1 $tag" -ForegroundColor DarkGray
