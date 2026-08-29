@echo off
chcp 65001 >nul
title LiveStudio 第二台电脑环境检查
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-SecondMachineValidation.ps1"
echo.
echo 检查完成，报告位于当前目录的 results 文件夹。
pause
