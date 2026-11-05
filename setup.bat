@echo off
setlocal
title Ambient Desktop - Setup
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\install.ps1" %*
set "ERR=%ERRORLEVEL%"
if not "%ERR%"=="0" (
    echo.
    echo   Setup did not finish. Press any key to close.
    pause >nul
    exit /b %ERR%
)
timeout /t 8
exit /b 0
