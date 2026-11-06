@echo off
chcp 65001 >nul
rem 上传 / 更新创意工坊物品。用法： publish.bat 你的Steam账号
rem 第一次会要求输入 Steam 密码和邮箱验证码，之后会记住登录。

if "%~1"=="" (
    echo 用法： publish.bat 你的Steam账号
    pause
    exit /b 1
)

set "STEAMCMD=C:\steamcmd\steamcmd.exe"
if not exist "%STEAMCMD%" (
    set "STEAMCMD=%ProgramFiles(x86)%\Steam\steamcmd.exe"
)
if not exist "%STEAMCMD%" (
    echo 找不到 steamcmd.exe。
    echo 先到这里下载： https://developer.valvesoftware.com/wiki/SteamCMD
    echo 解压到 C:\steamcmd 后重新运行本文件。
    pause
    exit /b 1
)

if not exist "%~dp0workshop\mod.vdf" (
    echo 找不到 workshop\mod.vdf，先运行一次 package.ps1。
    pause
    exit /b 1
)

echo 正在上传，控制台会打印物品 ID……
"%STEAMCMD%" +login %1 +workshop_build_item "%~dp0workshop\mod.vdf" +quit
echo.
echo 完成后 mod.vdf 里的 publishedfileid 会被填入物品 ID，下次跑同一个文件就是更新。
pause
