<#
    把模组打包成「可以直接上传创意工坊」的文件夹。

    用法：
        package.bat              # 编译 + 打包
        package.bat -SkipBuild   # 只重新打包，不编译

    产物：
        dist\workshop\     ← 上传内容（manifest.xml + QoL.dll + 图片 + preview.png）
        workshop\mod.vdf   ← steamcmd 用的物品描述文件，只在第一次生成；
                             之后 steamcmd 会把物品 ID 写回去，别覆盖它

    上传：
        publish.bat 你的Steam账号
#>
param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$game = 'C:\Program Files (x86)\Steam\steamapps\common\Probably Stolen'
$src = Join-Path $root 'mod\QoL'
$out = Join-Path $root 'dist\workshop'
$vdfDir = Join-Path $root 'workshop'
$vdf = Join-Path $vdfDir 'mod.vdf'
$preview = Join-Path $vdfDir 'preview.png'
$previewJpg = Join-Path $vdfDir 'preview.jpg'

if (-not $SkipBuild) {
    Write-Host '编译中…' -ForegroundColor Cyan
    & "$root\.tools\dotnet\dotnet.exe" build "$src\QoL.csproj" -c Release "-p:GameDir=$game" | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host '编译失败，未打包。' -ForegroundColor Red
        exit 2
    }
}

$dll = Join-Path $src 'bin\Release\QoL.dll'
if (-not (Test-Path $dll)) {
    Write-Host "找不到 $dll，先编译一次。" -ForegroundColor Red
    exit 2
}

# 每次重新生成，避免上一版的残留文件被一起传上去
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

Copy-Item $dll (Join-Path $out 'QoL.dll')
Copy-Item (Join-Path $src 'manifest.xml') (Join-Path $out 'manifest.xml')

# 模组自带的图片资源（框选统计的遮罩）。
# 必须放在 Textures\ 子目录：游戏启动时会把 Mods/<id>/Textures/*.png 读进内存，
# 模组从 LoadedMod.Textures 取，这样代码里不需要任何文件接口（省掉 files 权限）。
$assets = Join-Path $src 'Assets'
if (Test-Path $assets) {
    $texOut = Join-Path $out 'Textures'
    New-Item -ItemType Directory -Path $texOut -Force | Out-Null
    Get-ChildItem $assets -Filter *.png -File -Recurse | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $texOut $_.Name) -Force
    }
}
# 旧位置（根目录）的图不再使用，打包时清掉，免得混淆
Get-ChildItem $out -Filter *.png -File | Where-Object { $_.Name -ne 'preview.png' } | Remove-Item -Force

# 封面图不进内容包：封面（preview.jpg）只放在 workshop\ 下，由网页「添加/编辑图片」上传。
# 这样玩家的下载包里没有重复图片，体积最小。
if ((Test-Path $preview) -and ((Get-Item $preview).Length -gt 1MB)) {
    Write-Host '提示：workshop\preview.png 超过 1 MB，网页上传可能被拒，建议改用 preview.jpg。' -ForegroundColor Yellow
}

if (-not (Test-Path $vdfDir)) { New-Item -ItemType Directory -Path $vdfDir -Force | Out-Null }

# 只在第一次生成：steamcmd 会把物品 ID 写回这个文件，覆盖就等于每次新建物品
if (-not (Test-Path $vdf)) {
    $title = '深空当铺 · 便利整合（QoL Bundle）'

    $descLines = @(
        '深空当铺 · 便利整合（QoL Bundle）',
        '',
        '一站式体验优化：价格趋势、真伪自动揭穿、成交金额与声望播报、到店提示、对话加速、箱子批量贴标签、荧光莓品质角标、物品详情、长按框选统计、全自动换电池、机器内部与电力提示。',
        '',
        '每项功能都能在设置面板里单独开关，改完立即生效；不碰存档。',
        '',
        '有问题或建议请在下方评论。'
    )
    $desc = ($descLines -join '\n') -replace '"', "'"

    # VDF 里的路径用正斜杠，省得双写反斜杠
    $contentPath = ($out -replace '\\', '/')
    $previewPath = ((Join-Path $out 'preview.png') -replace '\\', '/')

    # 没有缩略图时不要写 previewfile 这一行，否则 steamcmd 找不到文件会报错
    $previewLine = ''
    if (Test-Path $preview) {
        $previewLine = '    "previewfile"      "' + $previewPath + '"'
    }

    $template = @"
"workshopitem"
{
    "appid"            "4348910"
    "publishedfileid"  "0"
    "contentfolder"    "$contentPath"
$previewLine
    "visibility"       "2"
    "title"            "$title"
    "description"      "$desc"
    "changenote"       "首次发布"
}
"@
    # 不要 BOM：steamcmd 的 VDF 解析不认 BOM
    [System.IO.File]::WriteAllText($vdf, $template, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host '已生成 workshop\mod.vdf（visibility=2 隐藏，测试没问题再改成 0 公开）。' -ForegroundColor Green
}

$files = Get-ChildItem $out -File
Write-Host ''
Write-Host "打包完成：$out" -ForegroundColor Green
foreach ($f in $files) {
    Write-Host ('  {0,-40} {1,8:N0} KB' -f $f.Name, ($f.Length / 1KB))
}
Write-Host ('  合计 {0:N1} MB' -f (($files | Measure-Object Length -Sum).Sum / 1MB))
Write-Host ''
Write-Host '上传：publish.bat 你的Steam账号' -ForegroundColor Cyan
