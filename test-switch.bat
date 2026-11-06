@echo off
rem 在「本地版」和「工坊版」之间切换，用来测试创意工坊上真正下载到的那份。
rem 双击运行：有 qol 就改成 qol_local（切到工坊版）；有 qol_local 就改回 qol（切回本地版）。

set "MODDIR=C:\Program Files (x86)\Steam\steamapps\common\Probably Stolen\Mods"

tasklist /fi "imagename eq Probably Stolen.exe" 2>nul | find /i "Probably Stolen.exe" >nul
if not errorlevel 1 goto :running

if not exist "%MODDIR%" goto :nodir
if exist "%MODDIR%\qol_local" goto :toLocal
if exist "%MODDIR%\qol" goto :toWorkshop

echo 既没有 Mods\qol 也没有 Mods\qol_local，检查一下游戏目录。
goto :end

:running
echo 游戏还在运行，先把游戏退出再运行本脚本。
goto :end

:nodir
echo 找不到游戏 Mods 目录：
echo "%MODDIR%"
goto :end

:toWorkshop
ren "%MODDIR%\qol" qol_local
echo 已切到【工坊版】：本地副本改名为 Mods\qol_local
echo 启动游戏，模组列表里会出现带 Steam 图标的「创意工坊」条目，激活后重启。
goto :end

:toLocal
ren "%MODDIR%\qol_local" qol
echo 已切回【本地版】：Mods\qol
echo 启动游戏即用你自己部署的那份。
goto :end

:end
pause
