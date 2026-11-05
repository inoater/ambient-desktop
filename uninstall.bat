@echo off
setlocal
title Ambient Desktop - Uninstall
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\uninstall.ps1" %*
timeout /t 5
