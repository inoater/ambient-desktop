# Ambient Desktop uninstaller - removes everything the installer added.
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\AmbientDesktop'),
    [switch]$KeepSettings
)

$ErrorActionPreference = 'SilentlyContinue'
$AppName = 'Ambient Desktop'
$target = Join-Path $InstallDir 'AmbientDesktop.exe'

Write-Host ""
Write-Host "  Uninstalling $AppName..." -ForegroundColor Cyan

# Close it gracefully, then make sure.
if (Get-Process -Name 'AmbientDesktop') {
    if (Test-Path $target) { & $target --exit | Out-Null }
    $deadline = (Get-Date).AddSeconds(4)
    while ((Get-Process -Name 'AmbientDesktop') -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Get-Process -Name 'AmbientDesktop' | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'AmbientDesktop'
Remove-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AmbientDesktop' -Recurse -Force
Remove-Item -Path (Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk") -Force
if (-not $KeepSettings) {
    Remove-Item -Path (Join-Path $env:APPDATA 'AmbientDesktop') -Recurse -Force
}
Remove-Item -Path $InstallDir -Recurse -Force

Write-Host "  Removed. Your normal wallpaper is back." -ForegroundColor Green
Write-Host ""
