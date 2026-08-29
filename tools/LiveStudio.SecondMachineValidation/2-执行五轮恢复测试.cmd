@echo off
chcp 65001 >nul
title LiveStudio 第二台电脑五轮恢复测试
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-SecondMachineValidation.ps1" -ExecuteFiveRestoreCycles -CycleCount 5
echo.
echo 测试结束，报告位于当前目录的 results 文件夹。
pause
