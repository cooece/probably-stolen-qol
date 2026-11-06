@echo off
rem 上传 / 更新创意工坊物品。双击即可，会提示输入 Steam 账号。
rem 第一次会要求输入密码和 Steam Guard 验证码，之后会记住登录。

set "ACCOUNT=%~1"
if "%ACCOUNT%"=="" set /p ACCOUNT=请输入你的 Steam 账号（登录名）： 
if "%ACCOUNT%"=="" (
    echo 没有输入账号，已取消。
    pause
    exit /b 1
)

rem 按顺序在常见位置找 steamcmd.exe（Steam 目录里那份、独立解压的那份都算）
set "STEAMCMD="
for %%P in (
    "C:\steamcmd\steamcmd.exe"
    "C:\steamcmd\steamcmd\steamcmd.exe"
    "%ProgramFiles(x86)%\Steam\steamcmd\steamcmd.exe"
    "%ProgramFiles(x86)%\Steam\steamcmd.exe"
    "%~dp0steamcmd\steamcmd.exe"
) do if not defined STEAMCMD if exist %%~P set "STEAMCMD=%%~P"

if not defined STEAMCMD (
    echo 找不到 steamcmd.exe。
    echo 到 https://developer.valvesoftware.com/wiki/SteamCMD 下载，
    echo 解压到 C:\steamcmd 后重新运行本文件。
    pause
    exit /b 1
)

if not exist "%~dp0workshop\mod.vdf" (
    echo 找不到 workshop\mod.vdf，先运行一次 package.bat。
    pause
    exit /b 1
)

echo.
echo 使用 steamcmd：%STEAMCMD%
echo 账号：%ACCOUNT%
echo 正在上传，完成后会打印物品 ID……
echo.
"%STEAMCMD%" +login "%ACCOUNT%" +workshop_build_item "%~dp0workshop\mod.vdf" +quit
echo.
echo 上传结束。若成功，workshop\mod.vdf 里的 publishedfileid 已由 steamcmd 填上物品 ID；
echo 以后跑同一个文件就是更新这一条目，不要删它。
pause
