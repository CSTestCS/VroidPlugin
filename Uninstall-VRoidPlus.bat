@echo off
title VRoid Plus uninstaller
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\uninstall.ps1" %*
if errorlevel 1 pause
