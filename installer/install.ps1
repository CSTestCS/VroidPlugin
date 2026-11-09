# VRoid Plus installer. Run through Install-VRoidPlus.bat (double-click).
#  1. Finds VRoid Studio (Steam)            4. Gets a private copy of the .NET SDK if needed
#  2. Installs BepInEx 6 (IL2CPP)           5. Builds VRoid Plus against your VRoid version
#  3. Starts VRoid once so BepInEx can      6. Copies it into BepInEx\plugins
#     prepare (VRoid closes by itself)
param([string]$GameDir = "")

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # makes Invoke-WebRequest much faster on PowerShell 5
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$BepInExUrl  = 'https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip'
$ProcessName = 'VRoidStudio'
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$Project     = Join-Path $RepoRoot 'src\VRoidPlus\VRoidPlus.csproj'
$WorkDir     = Join-Path $env:LOCALAPPDATA 'VRoidPlus'

function Say([string]$m, [string]$c = 'Gray') { Write-Host $m -ForegroundColor $c }
function Read-Shared([string]$path) {
    # BepInEx keeps its log open while VRoid runs; open it without locking.
    try {
        $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
        try { return (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
    } catch { return '' }
}
function Step([string]$m) { Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }
function Fail([string]$m) {
    Write-Host ""; Write-Host "Installation failed: $m" -ForegroundColor Red
    Read-Host "Press Enter to close"; exit 1
}

Write-Host "VRoid Plus installer" -ForegroundColor White
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null

# ------------------------------------------------------------------ 1. find VRoid Studio
. (Join-Path $PSScriptRoot 'find-vroid.ps1')

Step "Looking for VRoid Studio"
if (-not $GameDir) { $GameDir = Find-VRoid }
if (-not $GameDir) {
    Say "Couldn't find VRoid Studio automatically. Please choose its folder (the one containing $ExeName)." Yellow
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = "Select the VRoid Studio folder (contains $ExeName)"
    if ($dlg.ShowDialog() -ne 'OK') { Fail "No folder chosen." }
    $GameDir = $dlg.SelectedPath
}
$GameDir = $GameDir.TrimEnd('\')
if (-not (Test-Path (Join-Path $GameDir $ExeName))) { Fail "$ExeName not found in '$GameDir'." }
if (-not (Test-Path (Join-Path $GameDir 'GameAssembly.dll'))) {
    Fail "This VRoid Studio is an old (Mono) build. VRoid Plus needs VRoid Studio 1.26.1 or newer - update it in Steam."
}
Say "Found: $GameDir" Green

$running = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
if ($running) {
    Say "VRoid Studio is running. Save your work and close it, then press Enter." Yellow
    Read-Host | Out-Null
    if (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue) { Fail "VRoid Studio is still running." }
}

# ------------------------------------------------------------------ 2. BepInEx
Step "Installing BepInEx 6 (mod loader)"
$bepCore = Join-Path $GameDir 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
if (Test-Path $bepCore) {
    Say "Already installed." Green
} else {
    if (Test-Path (Join-Path $GameDir 'BepInEx\core\BepInEx.dll')) {
        Say "An old BepInEx 5 install was found; it doesn't work with this VRoid version and will be replaced (your other plugins stay in BepInEx\plugins)." Yellow
    }
    $zip = Join-Path $WorkDir 'BepInEx.zip'
    try { Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing }
    catch { Fail "Could not download BepInEx: $($_.Exception.Message)" }
    Expand-Archive -Path $zip -DestinationPath $GameDir -Force
    Remove-Item $zip -Force
    if (-not (Test-Path $bepCore)) { Fail "BepInEx did not extract correctly." }
    Say "Installed." Green
}

# ------------------------------------------------------------------ 3. first launch (generates interop assemblies)
$interop = Join-Path $GameDir 'BepInEx\interop'
$coreModule = Join-Path $interop 'UnityEngine.CoreModule.dll'
$gameAsm = Join-Path $GameDir 'GameAssembly.dll'
$needGen = -not (Test-Path $coreModule)
if (-not $needGen -and (Get-Item $gameAsm).LastWriteTime -gt (Get-Item $coreModule).LastWriteTime) {
    Say "VRoid Studio was updated since BepInEx last prepared it." Yellow
    $needGen = $true
}
if ($needGen) {
    Step "Preparing BepInEx for your VRoid version"
    Say "VRoid Studio will now start once. The first start with BepInEx takes 1-5 minutes (a console window appears)." Yellow
    Say "Don't use VRoid yet - this installer closes it automatically when ready." Yellow
    $log = Join-Path $GameDir 'BepInEx\LogOutput.log'
    if (Test-Path $log) { Remove-Item $log -Force -ErrorAction SilentlyContinue }
    Start-Process -FilePath (Join-Path $GameDir $ExeName) -WorkingDirectory $GameDir | Out-Null
    $deadline = (Get-Date).AddMinutes(20)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $logText = ''
        if (Test-Path $log) { $logText = Read-Shared $log }
        $fresh = (Test-Path $coreModule) -and ((Get-Item $coreModule).LastWriteTime -ge (Get-Item $gameAsm).LastWriteTime)
        if ($fresh -and ($logText -match 'Chainloader')) { $ready = $true; break }
        if ($fresh -and -not (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)) { $ready = $true; break }
        Write-Host -NoNewline "."
    }
    Write-Host ""
    Start-Sleep -Seconds 3
    Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    Start-Sleep -Seconds 4
    Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if (-not $ready) { Fail "BepInEx didn't finish preparing. Check $log, then run this installer again." }
    Say "Ready." Green
}

# ------------------------------------------------------------------ 4. .NET SDK (private copy, no admin rights)
Step "Getting the build tools"
$dotnet = $null
$sys = Get-Command dotnet -ErrorAction SilentlyContinue
if ($sys) {
    $sdks = & $sys.Source --list-sdks 2>$null
    if ($sdks | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 6 }) { $dotnet = $sys.Source }
}
$privateDotnet = Join-Path $WorkDir 'dotnet\dotnet.exe'
if (-not $dotnet -and (Test-Path $privateDotnet)) { $dotnet = $privateDotnet }
if (-not $dotnet) {
    Say "Downloading the .NET SDK (about 200 MB, only needed once; stored in $WorkDir)..."
    $installer = Join-Path $WorkDir 'dotnet-install.ps1'
    try { Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing }
    catch { Fail "Could not download the .NET installer: $($_.Exception.Message)" }
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Channel 8.0 -InstallDir (Join-Path $WorkDir 'dotnet') -NoPath | Out-Host
    if (-not (Test-Path $privateDotnet)) { Fail ".NET SDK installation failed." }
    $dotnet = $privateDotnet
}
Say "Using $dotnet" Green

# ------------------------------------------------------------------ 5. build
Step "Building VRoid Plus for your VRoid version"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$out = Join-Path $WorkDir 'build'
$buildLog = Join-Path $WorkDir 'build.log'
& $dotnet build $Project -c Release "-p:GameDir=$GameDir" -o $out --nologo 2>&1 | Tee-Object -FilePath $buildLog | Select-String -Pattern 'error|Build succeeded' | ForEach-Object { Say $_.Line }
$dll = Join-Path $out 'VRoidPlus.dll'
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dll)) {
    $prebuilt = Join-Path $RepoRoot 'prebuilt\VRoidPlus.dll'
    if (Test-Path $prebuilt) { Say "Build failed; using the prebuilt copy instead." Yellow; $dll = $prebuilt }
    else { Fail "Build failed. Full log: $buildLog (please attach it when reporting the problem)." }
}

# ------------------------------------------------------------------ 6. install plugin
Step "Installing the plugin"
$pluginDir = Join-Path $GameDir 'BepInEx\plugins\VRoidPlus'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item $dll $pluginDir -Force
Say "Installed to $pluginDir" Green

Write-Host ""
Write-Host "Done! Start VRoid Studio from Steam as usual." -ForegroundColor Green
Write-Host "Press F8 (or click 'VRoid+' in the bottom-left corner) to open VRoid Plus." -ForegroundColor Green
Write-Host "If VRoid Studio updates and VRoid Plus stops working, just run this installer again." -ForegroundColor Gray
Read-Host "Press Enter to close"
