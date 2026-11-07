@echo off
rem 在【本地版】和【工坊版】之间切换。
rem 关键：加载器会扫描 Mods 下的所有子文件夹，改名不算停用，必须整份移出 Mods。
rem 本地版 → Mods\qol          工坊版 → 本地副本放在 _mods_off\qol（不参与扫描）

set "GAME=C:\Program Files (x86)\Steam\steamapps\common\Probably Stolen"
set "MODDIR=%GAME%\Mods"
set "PARK=%GAME%\_mods_off"

tasklist /fi "imagename eq Probably Stolen.exe" 2>nul | find /i "Probably Stolen.exe" >nul
if not errorlevel 1 goto :running

if exist "%MODDIR%\qol\manifest.xml" goto :toWorkshop
if exist "%PARK%\qol\manifest.xml" goto :toLocal
if exist "%MODDIR%\qol_local\manifest.xml" goto :migrate
echo 既找不到 Mods\qol，也找不到 _mods_off\qol，检查一下游戏目录。
goto :end

:migrate
if not exist "%PARK%" mkdir "%PARK%"
move "%MODDIR%\qol_local" "%PARK%\qol" >nul
echo 已把旧的 qol_local 搬到 _mods_off\qol（它之前一直还在加载，改名没用）。
echo 现在处于【工坊版】：Mods 里没有本地副本，游戏会加载创意工坊那份。
goto :end

:toWorkshop
if not exist "%PARK%" mkdir "%PARK%"
move "%MODDIR%\qol" "%PARK%\qol" >nul
echo 已切到【工坊版】：本地副本移到 _mods_off\qol
echo 启动游戏，模组列表里那条会带 Steam 图标。
goto :end

:toLocal
if not exist "%MODDIR%" mkdir "%MODDIR%"
move "%PARK%\qol" "%MODDIR%\qol" >nul
echo 已切回【本地版】：本地副本回到 Mods\qol
echo 启动游戏即用你自己部署的那份。
goto :end

:running
echo 游戏还在运行，先退出游戏再运行本脚本。
goto :end

:end
pause
