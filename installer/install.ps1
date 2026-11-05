# Ambient Desktop installer
# Builds the app from source with the C# compiler that ships inside Windows (.NET Framework 4.x),
# installs it per-user (no admin needed), adds a Start Menu shortcut, an Apps & Features entry,
# optionally starts it with Windows, and launches it.

param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\AmbientDesktop'),
    [switch]$NoStartup,
    [switch]$NoLaunch,
    [switch]$NoShortcuts,
    [switch]$NoRegister
)

$ErrorActionPreference = 'Stop'
$AppName = 'Ambient Desktop'
$ExeName = 'AmbientDesktop.exe'
$Version = '1.0.0'
$Root = Split-Path -Parent $PSScriptRoot

function Step($text) { Write-Host "  > $text" -ForegroundColor Cyan }
function Ok($text) { Write-Host "    $text" -ForegroundColor DarkGray }
function Fail($text) {
    Write-Host ""
    Write-Host "  X $text" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "  ==============================================" -ForegroundColor DarkCyan
Write-Host "       A M B I E N T   D E S K T O P   setup" -ForegroundColor White
Write-Host "  ==============================================" -ForegroundColor Magenta
Write-Host ""

# 1. Compiler --------------------------------------------------------------------------
Step "Looking for the built-in C# compiler"
$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    Fail ".NET Framework 4.x was not found. It ships with Windows 10/11 - turn it on under 'Turn Windows features on or off'."
}
Ok $csc

$srcDir = Join-Path $Root 'src'
$sources = @(Get-ChildItem -Path $srcDir -Filter '*.cs' | ForEach-Object { $_.FullName })
if ($sources.Count -eq 0) { Fail "No source files found in $srcDir" }

# 2. Stop a running copy ---------------------------------------------------------------
$target = Join-Path $InstallDir $ExeName
$running = @(Get-Process -Name 'AmbientDesktop' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Step "Closing the running copy"
    if (Test-Path $target) { & $target --exit | Out-Null }
    $deadline = (Get-Date).AddSeconds(4)
    while ((Get-Process -Name 'AmbientDesktop' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    Get-Process -Name 'AmbientDesktop' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
}

# 3. Build -----------------------------------------------------------------------------
Step "Building $ExeName"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$buildDir = Join-Path $env:TEMP ('AmbientDesktopBuild_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
$builtExe = Join-Path $buildDir $ExeName

$rsp = @(
    '/nologo', '/target:winexe', '/optimize+', '/unsafe', '/platform:anycpu',
    ('/out:"{0}"' -f $builtExe),
    ('/win32manifest:"{0}"' -f (Join-Path $srcDir 'app.manifest')),
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'
)
$icon = Join-Path $Root 'assets\AmbientDesktop.ico'
if (Test-Path $icon) { $rsp += ('/win32icon:"{0}"' -f $icon) }
$rsp += ($sources | ForEach-Object { '"{0}"' -f $_ })
$rspFile = Join-Path $buildDir 'build.rsp'
[IO.File]::WriteAllLines($rspFile, [string[]]$rsp)

$output = & $csc "@$rspFile" 2>&1
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $builtExe)) {
    $output | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
    Remove-Item -Recurse -Force $buildDir -ErrorAction SilentlyContinue
    Fail "Build failed (see compiler output above)."
}
Ok "compiled OK"

# 4. Install files ---------------------------------------------------------------------
Step "Installing to $InstallDir"
Copy-Item -Force $builtExe $target
Copy-Item -Force (Join-Path $PSScriptRoot 'uninstall.ps1') (Join-Path $InstallDir 'uninstall.ps1')
if (Test-Path $icon) { Copy-Item -Force $icon (Join-Path $InstallDir 'AmbientDesktop.ico') }
Remove-Item -Recurse -Force $buildDir -ErrorAction SilentlyContinue
$uninstallCmd = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}"' -f (Join-Path $InstallDir 'uninstall.ps1')
Ok "files copied"

# 5. Shortcuts -------------------------------------------------------------------------
if (-not $NoShortcuts) {
    Step "Adding Start Menu shortcut"
    $programs = [Environment]::GetFolderPath('Programs')
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut((Join-Path $programs "$AppName.lnk"))
    $lnk.TargetPath = $target
    $lnk.WorkingDirectory = $InstallDir
    $lnk.Description = 'Black desktop with YouTube-style ambient glow around your windows'
    $lnk.IconLocation = "$target,0"
    $lnk.Save()
    Ok "Start > $AppName"
}

# 6. Apps & Features entry -------------------------------------------------------------
if (-not $NoRegister) {
    Step "Registering in Apps & Features"
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AmbientDesktop'
    New-Item -Path $key -Force | Out-Null
    $size = [int]((Get-Item $target).Length / 1KB)
    Set-ItemProperty -Path $key -Name DisplayName -Value $AppName
    Set-ItemProperty -Path $key -Name DisplayVersion -Value $Version
    Set-ItemProperty -Path $key -Name Publisher -Value 'Ambient Desktop'
    Set-ItemProperty -Path $key -Name DisplayIcon -Value "$target,0"
    Set-ItemProperty -Path $key -Name InstallLocation -Value $InstallDir
    Set-ItemProperty -Path $key -Name UninstallString -Value $uninstallCmd
    Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord
    Set-ItemProperty -Path $key -Name EstimatedSize -Value $size -Type DWord
    Ok "uninstall from Settings > Apps any time"
}

# 7. Start with Windows ----------------------------------------------------------------
if (-not $NoStartup) {
    Step "Starting with Windows"
    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Set-ItemProperty -Path $run -Name 'AmbientDesktop' -Value ('"{0}"' -f $target)
    Ok "on (toggle it from the tray menu)"
}

# 8. Launch ----------------------------------------------------------------------------
if (-not $NoLaunch) {
    Step "Launching"
    Start-Process -FilePath $target -WorkingDirectory $InstallDir
}

Write-Host ""
Write-Host "  Done! Your desktop is now black with ambient glow." -ForegroundColor Green
Write-Host "  Tray icon: right-click for intensity, glow size, transitions, taskbar glow, performance." -ForegroundColor Gray
Write-Host "             double-click to toggle on/off." -ForegroundColor Gray
Write-Host ""
exit 0
