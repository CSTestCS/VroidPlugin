# VRoid Plus installer. Run through Install-VRoidPlus.bat (double-click).
#  1. Finds VRoid Studio (Steam)            4. Gets a private copy of the .NET SDK if needed
#  2. Installs BepInEx 6 (IL2CPP)           5. Builds VRoid Plus against your VRoid version
#  3. Starts VRoid once so BepInEx can      6. Copies it into BepInEx\plugins
#     prepare (VRoid closes by itself)      7. Starts VRoid and checks that VRoid Plus loaded
# Everything shown here is also written to %LOCALAPPDATA%\VRoidPlus\install-log.txt
param([string]$GameDir = "")

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # makes Invoke-WebRequest much faster on PowerShell 5
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

$BepInExUrl  = 'https://builds.bepinex.dev/projects/bepinex_be/793/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.793%2Bdbc82f0.zip'
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$Project     = Join-Path $RepoRoot 'src\VRoidPlus\VRoidPlus.csproj'
$WorkDir     = Join-Path $env:LOCALAPPDATA 'VRoidPlus'
$InstallLog  = Join-Path $WorkDir 'install-log.txt'

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
try { Start-Transcript -Path $InstallLog -Force | Out-Null } catch { }

function Say([string]$m, [string]$c = 'Gray') { Write-Host $m -ForegroundColor $c }
function Step([string]$m) { Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }
function Finish([int]$code) {
    try { Stop-Transcript | Out-Null } catch { }
    Write-Host ""
    Read-Host "Press Enter to close"
    exit $code
}
function Fail([string]$m) {
    Write-Host ""
    Write-Host "Installation failed: $m" -ForegroundColor Red
    Write-Host "A full log was saved to: $InstallLog" -ForegroundColor Yellow
    Write-Host "Please send that file when reporting the problem." -ForegroundColor Yellow
    Finish 1
}
# Any unexpected error: show it and keep the window open instead of closing silently.
trap {
    Write-Host ""
    Write-Host "Unexpected error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ("  at line " + $_.InvocationInfo.ScriptLineNumber + ": " + $_.InvocationInfo.Line.Trim()) -ForegroundColor DarkGray
    Fail "see the error above."
}

function Read-Shared([string]$path) {
    # BepInEx keeps its log open while VRoid runs; read it without locking.
    try {
        $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
        try { return (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
    } catch { return '' }
}

# Runs a command-line program. Windows PowerShell 5 turns anything a program prints to stderr into
# a fatal error when ErrorActionPreference is Stop, so relax it just for the call.
function Invoke-Tool([string]$exe, [string[]]$arguments, [string]$logFile) {
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($logFile) { & $exe @arguments 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $logFile | Out-Null }
        else { & $exe @arguments 2>&1 | ForEach-Object { "$_" } | Out-Host }
        return $LASTEXITCODE
    } finally { $ErrorActionPreference = $old }
}

function Get-VRoidProcesses([string]$exePath) {
    $name = [IO.Path]::GetFileNameWithoutExtension($exePath)
    return @(Get-Process -Name $name -ErrorAction SilentlyContinue)
}

function Stop-VRoid([string]$exePath) {
    Get-VRoidProcesses $exePath | ForEach-Object { try { $_.CloseMainWindow() | Out-Null } catch { } }
    Start-Sleep -Seconds 5
    Get-VRoidProcesses $exePath | Stop-Process -Force -ErrorAction SilentlyContinue
}

Write-Host "VRoid Plus installer" -ForegroundColor White
Say "Windows PowerShell $($PSVersionTable.PSVersion)   installer folder: $RepoRoot" DarkGray
if (-not (Test-Path -LiteralPath $Project)) { Fail "Files are missing ($Project). Extract the whole ZIP first, then run Install-VRoidPlus.bat from the extracted folder." }

# ------------------------------------------------------------------ 1. find VRoid Studio
. (Join-Path $PSScriptRoot 'find-vroid.ps1')

Step "Looking for VRoid Studio"
if (-not $GameDir) { $GameDir = Find-VRoid }
if (-not $GameDir) {
    Say "Couldn't find VRoid Studio automatically." Yellow
    Say "A folder window has opened (check the taskbar if you don't see it). Choose the VRoid Studio folder." Yellow
    Say "Tip: in Steam, right-click VRoid Studio > Manage > Browse local files shows that folder." Yellow
    $GameDir = Select-VRoidFolder "Select the VRoid Studio folder (the one with VRoidStudio.exe)"
    if (-not $GameDir) { Fail "No folder was chosen." }
}
$GameDir = $GameDir.TrimEnd('\')
$Exe = Get-VRoidExe $GameDir
if (-not $Exe) { Fail "VRoid Studio's program wasn't found in '$GameDir'. Choose the folder that contains VRoidStudio.exe." }
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'GameAssembly.dll'))) {
    Fail "This copy of VRoid Studio is an old version (before 1.26.1). Update VRoid Studio in Steam and run this again."
}
Say "Found: $Exe" Green

if ((Get-VRoidProcesses $Exe).Count -gt 0) {
    Say "VRoid Studio is running. Save your work and close it, then press Enter." Yellow
    Read-Host | Out-Null
    if ((Get-VRoidProcesses $Exe).Count -gt 0) { Fail "VRoid Studio is still running." }
}

# ------------------------------------------------------------------ 2. BepInEx
Step "Installing BepInEx 6 (mod loader)"
$bepCore = Join-Path $GameDir 'BepInEx\core\BepInEx.Unity.IL2CPP.dll'
if (Test-Path -LiteralPath $bepCore) {
    Say "Already installed." Green
} else {
    if (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx\core\BepInEx.dll')) {
        Say "An old BepInEx 5 install was found. It doesn't work with this VRoid version and will be replaced (other plugins in BepInEx\plugins are kept)." Yellow
    }
    $zip = Join-Path $WorkDir 'BepInEx.zip'
    Say "Downloading BepInEx..."
    try { Invoke-WebRequest -Uri $BepInExUrl -OutFile $zip -UseBasicParsing }
    catch { Fail "Could not download BepInEx: $($_.Exception.Message)" }
    try { Expand-Archive -LiteralPath $zip -DestinationPath $GameDir -Force }
    catch { Fail "Could not unpack BepInEx into '$GameDir': $($_.Exception.Message)" }
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $bepCore)) { Fail "BepInEx did not unpack correctly." }
    Say "Installed." Green
}

# ------------------------------------------------------------------ 3. first launch (BepInEx generates the files the plugin is built against)
$bepLog     = Join-Path $GameDir 'BepInEx\LogOutput.log'
$coreModule = Join-Path $GameDir 'BepInEx\interop\UnityEngine.CoreModule.dll'
$gameAsm    = Join-Path $GameDir 'GameAssembly.dll'
function Test-InteropFresh {
    (Test-Path -LiteralPath $coreModule) -and ((Get-Item -LiteralPath $coreModule).LastWriteTime -ge (Get-Item -LiteralPath $gameAsm).LastWriteTime)
}
if (-not (Test-InteropFresh)) {
    Step "Preparing BepInEx for your VRoid version"
    Say "VRoid Studio will start now. The first start with BepInEx takes 1-10 minutes and shows a black console window." Yellow
    Say "Don't use VRoid yet. This installer closes it automatically when it's ready." Yellow
    Remove-Item -LiteralPath $bepLog -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath $Exe -WorkingDirectory $GameDir | Out-Null
    $start = Get-Date
    $deadline = $start.AddMinutes(25)
    $ready = $false; $seenRunning = $false; $lastNote = $start
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $running = (Get-VRoidProcesses $Exe).Count -gt 0
        if ($running) { $seenRunning = $true }
        $logText = Read-Shared $bepLog
        if ((Test-InteropFresh) -and ($logText -match 'Chainloader')) { $ready = $true; break }
        if ((Test-InteropFresh) -and $seenRunning -and -not $running) { $ready = $true; break }
        if (-not $running -and ((Get-Date) - $start).TotalSeconds -gt 90 -and -not (Test-Path -LiteralPath $bepLog)) {
            Fail "VRoid Studio closed (or never started) and BepInEx didn't run. Start VRoid once from Steam, close it, then run this installer again."
        }
        if (((Get-Date) - $lastNote).TotalSeconds -ge 20) {
            $lastNote = Get-Date
            $mins = [int]((Get-Date) - $start).TotalMinutes
            $state = if (-not $running) { 'waiting for VRoid to start' } elseif ($logText -match 'interop|Generating|Cpp2IL|unity libs|Unity base') { 'BepInEx is generating files' } else { 'VRoid is starting' }
            Say ("  still working ({0} min): {1}" -f $mins, $state) DarkGray
        }
    }
    Stop-VRoid $Exe
    if (-not $ready) {
        if (Test-Path -LiteralPath $bepLog) { Copy-Item -LiteralPath $bepLog (Join-Path $WorkDir 'BepInEx-LogOutput.log') -Force -ErrorAction SilentlyContinue }
        Fail "BepInEx didn't finish preparing in time. Its log was copied to $WorkDir\BepInEx-LogOutput.log."
    }
    Say "Ready." Green
}

# ------------------------------------------------------------------ 4. .NET SDK (private copy, no admin rights)
Step "Getting the build tools"
$dotnet = $null
$sys = Get-Command dotnet -ErrorAction SilentlyContinue
if ($sys) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $sdks = & $sys.Source --list-sdks 2>$null
    $ErrorActionPreference = $old
    if ($sdks | Where-Object { "$_" -match '^(\d+)\.' -and [int]$Matches[1] -ge 6 }) { $dotnet = $sys.Source }
}
$privateDotnet = Join-Path $WorkDir 'dotnet\dotnet.exe'
if (-not $dotnet -and (Test-Path -LiteralPath $privateDotnet)) { $dotnet = $privateDotnet }
if (-not $dotnet) {
    Say "Downloading the .NET SDK (about 200 MB, only needed once; kept in $WorkDir)..."
    $dotnetInstaller = Join-Path $WorkDir 'dotnet-install.ps1'
    try { Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $dotnetInstaller -UseBasicParsing }
    catch { Fail "Could not download the .NET installer: $($_.Exception.Message)" }
    $code = Invoke-Tool 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $dotnetInstaller, '-Channel', '8.0', '-InstallDir', (Join-Path $WorkDir 'dotnet'), '-NoPath') $null
    if (-not (Test-Path -LiteralPath $privateDotnet)) { Fail ".NET SDK installation failed (exit code $code)." }
    $dotnet = $privateDotnet
}
Say "Using $dotnet" Green

# ------------------------------------------------------------------ 5. build
Step "Building VRoid Plus for your VRoid version (about a minute)"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$out = Join-Path $WorkDir 'build'
$buildLog = Join-Path $WorkDir 'build.log'
Remove-Item -LiteralPath $out -Recurse -Force -ErrorAction SilentlyContinue
$code = Invoke-Tool $dotnet @('build', $Project, '-c', 'Release', "-p:GameDir=$GameDir", '-o', $out, '--nologo') $buildLog
$dll = Join-Path $out 'VRoidPlus.dll'
if ($code -ne 0 -or -not (Test-Path -LiteralPath $dll)) {
    Get-Content -LiteralPath $buildLog -ErrorAction SilentlyContinue | Select-String -Pattern ': error ' | Select-Object -First 15 | ForEach-Object { Say $_.Line Red }
    $prebuilt = Join-Path $RepoRoot 'prebuilt\VRoidPlus.dll'
    if (Test-Path -LiteralPath $prebuilt) { Say "Build failed; using the prebuilt copy instead." Yellow; $dll = $prebuilt }
    else { Fail "Build failed (exit code $code). Build log: $buildLog" }
} else { Say "Built." Green }

# ------------------------------------------------------------------ 6. install plugin
Step "Installing the plugin"
$pluginDir = Join-Path $GameDir 'BepInEx\plugins\VRoidPlus'
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
Copy-Item -LiteralPath $dll -Destination $pluginDir -Force
Say "Installed to $pluginDir" Green

# ------------------------------------------------------------------ 7. check that it loads
Step "Checking that VRoid Plus loads"
Say "VRoid Studio will start now and stay open. This takes up to a few minutes." Yellow
Remove-Item -LiteralPath $bepLog -Force -ErrorAction SilentlyContinue
Start-Process -FilePath $Exe -WorkingDirectory $GameDir | Out-Null
$start = Get-Date
$result = 'timeout'
while (((Get-Date) - $start).TotalMinutes -lt 6) {
    Start-Sleep -Seconds 3
    $logText = Read-Shared $bepLog
    if ($logText -match 'VRoid Plus [\d\.]+ loaded') {
        Start-Sleep -Seconds 15   # give the window code a moment to run in case it reports errors
        $logText = Read-Shared $bepLog
        $result = if ($logText -match '\[(OnGUI|Update|LateUpdate)\]') { 'runtime-error' } else { 'ok' }
        break
    }
    if ($logText -match 'Error loading \[VRoid Plus|Could not load.*VRoidPlus|VRoidPlus.*(Exception|error)') { $result = 'load-error'; break }
    if ($logText -match 'Chainloader startup complete') { Start-Sleep -Seconds 3; if ((Read-Shared $bepLog) -notmatch 'VRoid Plus [\d\.]+ loaded') { $result = 'not-loaded'; break } }
}
Copy-Item -LiteralPath $bepLog (Join-Path $WorkDir 'BepInEx-LogOutput.log') -Force -ErrorAction SilentlyContinue
$errorLines = (Read-Shared $bepLog) -split "`n" | Where-Object { $_ -match 'VRoid ?Plus|\[(OnGUI|Update|LateUpdate)\]' -and $_ -match 'Error|Exception|Fatal' } | Select-Object -First 8

Write-Host ""
switch ($result) {
    'ok' {
        Write-Host "Success! VRoid Plus is running in VRoid Studio." -ForegroundColor Green
        Write-Host "Click 'VRoid+' in the bottom-left corner of VRoid, or press Ctrl+Shift+V, to open it." -ForegroundColor Green
        Write-Host "From now on just start VRoid Studio from Steam as usual." -ForegroundColor Gray
    }
    'runtime-error' {
        Write-Host "VRoid Plus loaded, but its window reported errors:" -ForegroundColor Yellow
        $errorLines | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
        Write-Host "Please send $WorkDir\BepInEx-LogOutput.log" -ForegroundColor Yellow
    }
    default {
        $why = @{ 'load-error' = 'VRoid Plus failed to load.'; 'not-loaded' = 'BepInEx started but did not load VRoid Plus.'; 'timeout' = "BepInEx didn't report in time (VRoid may still be starting)." }[$result]
        Write-Host $why -ForegroundColor Yellow
        $errorLines | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
        Write-Host "Please send these two files: $InstallLog" -ForegroundColor Yellow
        Write-Host "                         and $WorkDir\BepInEx-LogOutput.log" -ForegroundColor Yellow
    }
}
Write-Host "If VRoid Studio updates and VRoid Plus stops working, run this installer again." -ForegroundColor Gray
Finish 0
