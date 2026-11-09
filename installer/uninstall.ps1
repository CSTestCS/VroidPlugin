# VRoid Plus uninstaller. Run through Uninstall-VRoidPlus.bat.
param([string]$GameDir = "")
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'find-vroid.ps1')
if (-not $GameDir) { $GameDir = Find-VRoid }
if (-not $GameDir) {
    Write-Host "Choose the VRoid Studio folder in the window that opened (check the taskbar if you don't see it)." -ForegroundColor Yellow
    $GameDir = Select-VRoidFolder "Select the VRoid Studio folder"
    if (-not $GameDir) { exit 1 }
}
$GameDir = $GameDir.TrimEnd('\')
$exe = Get-VRoidExe $GameDir
$procName = if ($exe) { [IO.Path]::GetFileNameWithoutExtension($exe) } else { 'VRoidStudio' }
if (Get-Process -Name $procName -ErrorAction SilentlyContinue) {
    Write-Host "Please close VRoid Studio first, then press Enter." -ForegroundColor Yellow; Read-Host | Out-Null
}

$plugin = Join-Path $GameDir 'BepInEx\plugins\VRoidPlus'
if (Test-Path $plugin) { Remove-Item $plugin -Recurse -Force; Write-Host "Removed VRoid Plus." -ForegroundColor Green }
else { Write-Host "VRoid Plus wasn't installed in $GameDir." }

$answer = Read-Host "Also remove BepInEx completely (only if you use no other VRoid mods)? [y/N]"
if ($answer -match '^[yY]') {
    $data = Join-Path $GameDir 'BepInEx\config\VRoidPlus'
    if (Test-Path $data) {
        $backup = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'VRoidPlus-backup'
        New-Item -ItemType Directory -Force -Path $backup | Out-Null
        Copy-Item (Join-Path $data '*') $backup -Recurse -Force
        Write-Host "Your face sets, hair library and settings were copied to $backup" -ForegroundColor Green
    }
    foreach ($item in 'BepInEx', 'dotnet', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt') {
        $p = Join-Path $GameDir $item
        if (Test-Path $p) { Remove-Item $p -Recurse -Force }
    }
    Write-Host "BepInEx removed. VRoid Studio is back to normal." -ForegroundColor Green
} else {
    Write-Host "BepInEx kept. Your saved face sets and hair library are still in BepInEx\config\VRoidPlus."
}
Read-Host "Press Enter to close"
