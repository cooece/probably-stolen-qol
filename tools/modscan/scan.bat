@echo off
chcp 65001 >nul
cd /d "%~dp0"
set DLL=%~1
if "%DLL%"=="" set DLL=C:/Program Files (x86)\Steam\steamapps\common\Probably Stolen\Mods\qol\QoL.dll
"..\..\.tools\dotnet\dotnet.exe" build -c Release -v q --nologo || pause & exit /b 1
"..\..\.tools\dotnet\dotnet.exe" bin\Release\net8.0\scantool.dll "%DLL%"
pause
