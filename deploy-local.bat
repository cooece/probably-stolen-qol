@echo off
rem 把最新构建部署到游戏里的本地副本，并把遮罩换成【蓝色】（配合 HUD 美化模组）。
rem 发布到工坊的包用的是原版米黄遮罩，两者互不影响。
rem 用法：先跑 package.bat，再双击本文件；之后启动游戏。

set "GAME=C:\Program Files (x86)\Steam\steamapps\common\Probably Stolen"
set "MODDIR=%GAME%\Mods"
set "PARK=%GAME%\_mods_off"
set "SRC=%~dp0dist\workshop"
set "TARGET="

if not exist "%SRC%\QoL.dll" goto :nobuild

if exist "%MODDIR%\qol" set "TARGET=%MODDIR%\qol"
if not defined TARGET if exist "%PARK%\qol" set "TARGET=%PARK%\qol"
if not defined TARGET set "TARGET=%MODDIR%\qol"
if not exist "%TARGET%" mkdir "%TARGET%"

tasklist /fi "imagename eq Probably Stolen.exe" 2>nul | find /i "Probably Stolen.exe" >nul
if not errorlevel 1 goto :running

copy /Y "%SRC%\QoL.dll" "%TARGET%\" >nul
copy /Y "%SRC%\manifest.xml" "%TARGET%\" >nul

if exist "%~dp0workshop\mask-blue.png" goto :bluemask
copy /Y "%SRC%\negociation_tablet_ScreenOverlay.png" "%TARGET%\" >nul
echo 遮罩：原版米黄（没找到 mask-blue.png）
goto :clean
:bluemask
copy /Y "%~dp0workshop\mask-blue.png" "%TARGET%\negociation_tablet_ScreenOverlay.png" >nul
echo 遮罩：蓝色（本机 HUD 配色）

:clean
rem 加载器会拒绝文件夹里任何没写进 <Assemblies> 的 DLL（例如 *_local.dll、*.prev），
rem 所以这里把除 QoL.dll 之外的动态库统一改成 .bak，既不影响加载也留个底。
for %%F in ("%TARGET%\*.dll") do if /i not "%%~nxF"=="QoL.dll" ren "%%~fF" "%%~nF.bak" >nul 2>nul
for %%F in ("%TARGET%\*.prev") do ren "%%~fF" "%%~nF.bak" >nul 2>nul

echo 已部署到：%TARGET%
if /i "%TARGET%"=="%PARK%\qol" echo 注意：本地副本现在在 _mods_off 里，属于停用状态；要启用请双击 test-switch.bat。
goto :end

:running
echo 游戏还在运行，先退出游戏。
goto :end

:nobuild
echo 找不到 dist\workshop\QoL.dll，先运行 package.bat。
goto :end

:end
pause
