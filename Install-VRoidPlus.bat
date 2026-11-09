@echo off
title VRoid Plus installer
if not exist "%~dp0src\VRoidPlus\VRoidPlus.csproj" (
  echo Please extract the whole ZIP file to a folder first, then run Install-VRoidPlus.bat from there.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\install.ps1" %*
if errorlevel 1 pause
