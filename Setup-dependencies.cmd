@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-dependencies.ps1"
if errorlevel 1 (
  echo.
  echo Dependency setup failed. Review the message above.
  pause
  exit /b 1
)
start "" "%~dp0M3U8-Video-Downloader.exe"
