@echo off
chcp 65001 >nul
cd /d "%~dp0"
python tools\check_deadcode.py
pause
