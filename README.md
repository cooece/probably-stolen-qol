# 深空当铺 · 便利整合（QoL Bundle）

《深空当铺：可能是偷的》（Probably Stolen，Steam AppID 4348910）的体验优化模组，模组 id `local.qol`。

创意工坊：[3834886720](https://steamcommunity.com/sharedfiles/filedetails/?id=3834886720)

所有功能只改 UI 与交互体验，**不动存档结构、不改联机数据、不修改游戏本体文件**。
作者只做单机体验优化，不做任何破坏公平性的东西。

## 功能

完整清单（含每一项的默认开关状态）见 [`mod/QoL/README.md`](mod/QoL/README.md)。

大致分为几类：

- **提示与信息**：提示框精简、彩色分类标签（默认关）、物品分类角标、顾客对话回顾、到店提示、真实价值与真伪提示、水质标签同步
- **交易与柜台**：柜台溢出分页、一键上柜台、前台杂物提醒、交易结果弹窗、需求高亮、估价自动报价
- **物品操作**：右键菜单扩展（快捷键绑定、右键升级）、双击开锁、便捷框选、自动丢弃单据、检查收纳
- **机器与容器**：解序器自动续卡、机器区/暗格多页扩容、容器反向取液、自动换电池、自动换滤芯、酿酒工坊
- **生活流**：外出归来背包归位、对话/打烊/摇铃确认、店内行情线

设置项都在游戏内的模组设置页，改完即时生效（个别项需重开窗口）。

## 构建

需要：

- .NET SDK 8（或用仓库里 `.tools` 的方案，见下）
- 游戏本体（提供编译期引用的程序集）
- Windows + PowerShell

```powershell
# 编译（把 GameDir 指到你自己的游戏目录）
dotnet build mod/QoL/QoL.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Probably Stolen"

# 编译并直接部署到游戏的 Mods 目录
powershell -File mod/build.ps1
```

`build.ps1` 会把产物复制到 `<游戏目录>\Mods\qol\`，游戏重启后生效。

## 目录结构

```
mod/QoL/            模组源码（一个功能一个 .cs 文件，入口 ModEntry.cs）
mod/QoL/README.md   全部设置项与功能说明
mod/build.ps1       编译 + 部署
package.ps1         打包成可上传创意工坊的 dist/workshop/
publish.bat         用 steamcmd 上传工坊
version.ps1         编译 → 提交 → 打版本标签 → 记 CHANGELOG
rollback.ps1        回退到某个版本标签
tools/modscan/      离线跑游戏自带的 ModScanner，发布前确认权限声明
tools/check_deadcode.py  静态检查：没人引用的声明、设置项是否有人读
docs/               开发过程中的技术笔记
```

## 权限声明

模组 manifest 里 `<Capabilities />` 为空：不读写任意文件、不联网、不开后台线程、不使用反射访问外部程序集。
游戏内的模组列表不会出现黄色警告。

## 免责声明

- 非官方作品，与游戏开发商无关。
- 使用前请自行备份存档。
- 与其它模组可能存在交互冲突；已知会与修改同一 UI/交互入口的模组互相影响。
- 代码按现状提供，不保证适配未来的游戏版本。

## 许可

MIT，见 [LICENSE](LICENSE)。
