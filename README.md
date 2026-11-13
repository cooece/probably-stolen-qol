# 深空当铺 · 便利整合（QoL Bundle）

《深空当铺：可能是偷的》（Probably Stolen，Steam AppID 4348910）的体验优化模组，模组 id `local.qol`。

创意工坊：[3834886720](https://steamcommunity.com/sharedfiles/filedetails/?id=3834886720)

所有功能只改 UI 与交互体验，**不动存档结构、不改联机数据、不修改游戏本体文件**。

## 功能

完整清单（含每一项的默认开关状态）见 [`mod/QoL/README.md`](mod/QoL/README.md)。

- **提示与信息**：提示框精简、彩色分类标签（默认关）、物品分类角标、顾客对话回顾、到店提示、真实价值与真伪提示、水质标签同步
- **交易与柜台**：柜台溢出分页、一键上柜台、前台杂物提醒、交易结果弹窗、需求高亮、估价自动报价
- **物品操作**：右键菜单扩展（快捷键绑定、右键升级）、双击开锁、便捷框选、自动丢弃单据、检查收纳
- **机器与容器**：解序器自动续卡、机器区/暗格多页扩容、容器反向取液、自动换电池、自动换滤芯、酿酒工坊
- **生活流**：外出归来背包归位、对话/打烊/摇铃确认、店内行情线

设置项都在游戏内的模组设置页，改完即时生效（个别项需重开窗口）。

## 仓库内容

仓库只放模组本体，不含开发脚本、打包工具和创意工坊素材。

```
mod/QoL/QoL.csproj      工程文件
mod/QoL/ModEntry.cs     模组入口
mod/QoL/*.cs            一个功能一个文件
mod/QoL/manifest.xml    模组清单
mod/QoL/README.md       全部设置项与功能说明
mod/QoL/Assets/         UI 贴图
```

## 编译

需要 .NET SDK 8、游戏本体（提供编译期引用的程序集）、Windows：

```powershell
dotnet build mod/QoL/QoL.csproj -c Release `
  -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Probably Stolen"
```

产物连同 `manifest.xml` 一起放进 `<游戏目录>\Mods\qol\`，重启游戏生效。目录结构：

```
Mods\qol\
  ├─ QoL.dll                                  编译产物（bin\Release\QoL.dll）
  ├─ manifest.xml                             从 mod\QoL\ 复制
  └─ Textures\
       └─ negociation_tablet_ScreenOverlay.png  从 mod\QoL\Assets\ 复制
```

`Textures\` 里的贴图由游戏自己在启动时读进 `LoadedMod.Textures`，模组运行时按文件名取用；漏掉它，框选统计的遮罩会变成空白。

## 权限声明

`manifest.xml` 里 `<Capabilities />` 为空：不读写任意文件、不联网、不开后台线程、不使用反射访问外部程序集。游戏内的模组列表不会出现黄色警告。

## 免责声明

- 非官方作品，与游戏开发商无关。
- 使用前请自行备份存档。
- 与其它模组可能存在交互冲突；已知会与修改同一 UI/交互入口的模组互相影响。
- 代码按现状提供，不保证适配未来的游戏版本。

## 许可

MIT，见 [LICENSE](LICENSE)。
