@echo off
setlocal
set "SCRIPT=%~dp0Run-SecondMachineValidation.ps1"
set "PACKAGE=%~dp0LiveStudio-CrossMachine-Test.lscfg"
set "INSTALLER=%~dp0..\LiveStudio-Setup.exe"
set "RESULTS=%~dp0results"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -PackagePath "%PACKAGE%" -InstallerPath "%INSTALLER%" -OutputDirectory "%RESULTS%" -DiagnoseCapture
set "EXITCODE=%ERRORLEVEL%"
echo.
echo Diagnostic report directory: %RESULTS%
pause
exit /b %EXITCODE%
