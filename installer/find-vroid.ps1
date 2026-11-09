# Shared by install.ps1 and uninstall.ps1: finds the Steam copy of VRoid Studio.
$SteamAppId = '1486350'

# The VRoid program inside a folder: VRoidStudio.exe, or any Unity .exe that has a matching <name>_Data folder.
function Get-VRoidExe([string]$dir) {
    if (-not $dir -or -not (Test-Path -LiteralPath $dir)) { return $null }
    $preferred = [IO.Path]::Combine($dir, 'VRoidStudio.exe')
    if (Test-Path -LiteralPath $preferred) { return $preferred }
    foreach ($exe in (Get-ChildItem -LiteralPath $dir -Filter '*.exe' -File -ErrorAction SilentlyContinue)) {
        if ($exe.Name -match 'UnityCrashHandler|unins|setup') { continue }
        if (Test-Path -LiteralPath ([IO.Path]::Combine($dir, ($exe.BaseName + '_Data')))) { return $exe.FullName }
    }
    return $null
}

# Note: [IO.Path]::Combine is used instead of Join-Path because Join-Path fails on drives
# that don't exist (e.g. a Steam library on an unplugged disk).
function Find-VRoid {
    $candidates = New-Object System.Collections.Generic.List[string]
    $steam = $null
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        $p = Get-ItemProperty $key -ErrorAction SilentlyContinue
        if ($p -and $p.SteamPath) { $steam = $p.SteamPath; break }
        if ($p -and $p.InstallPath) { $steam = $p.InstallPath; break }
    }
    $libs = New-Object System.Collections.Generic.List[string]
    if ($steam) {
        $steam = $steam -replace '/', '\'
        $libs.Add($steam)
        $vdf = [IO.Path]::Combine($steam, 'steamapps\libraryfolders.vdf')
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in (Select-String -LiteralPath $vdf -Pattern '"path"\s+"([^"]+)"' -AllMatches)) {
                foreach ($mm in $m.Matches) { $libs.Add(($mm.Groups[1].Value -replace '\\\\', '\')) }
            }
        }
    }
    # Common library locations, in case the registry/vdf lookup fails.
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        foreach ($sub in 'SteamLibrary', 'Steam', 'Program Files (x86)\Steam', 'Program Files\Steam', 'Games\Steam', 'Games\SteamLibrary') {
            $libs.Add(([IO.Path]::Combine($drive.Root, $sub)))
        }
    }
    foreach ($lib in ($libs | Select-Object -Unique)) {
        try { if (-not (Test-Path -LiteralPath $lib)) { continue } } catch { continue }
        $acf = [IO.Path]::Combine($lib, "steamapps\appmanifest_$SteamAppId.acf")
        if (Test-Path -LiteralPath $acf) {
            $m = Select-String -LiteralPath $acf -Pattern '"installdir"\s+"([^"]+)"' | Select-Object -First 1
            if ($m) { $candidates.Add(([IO.Path]::Combine($lib, ("steamapps\common\" + $m.Matches[0].Groups[1].Value)))) }
        }
        $candidates.Add(([IO.Path]::Combine($lib, 'steamapps\common\VRoid Studio')))
        $candidates.Add(([IO.Path]::Combine($lib, 'steamapps\common\VRoidStudio')))
    }
    foreach ($c in $candidates) { try { if (Get-VRoidExe $c) { return $c } } catch { } }
    return $null
}

# Folder picker that opens in front of other windows.
function Select-VRoidFolder([string]$description) {
    Add-Type -AssemblyName System.Windows.Forms
    $owner = New-Object System.Windows.Forms.Form
    $owner.TopMost = $true
    $owner.ShowInTaskbar = $false
    $owner.WindowState = 'Minimized'
    $owner.Show(); $owner.Hide()
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = $description
    $dlg.ShowNewFolderButton = $false
    try {
        if ($dlg.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) { return $dlg.SelectedPath }
        return $null
    } finally { $owner.Dispose() }
}
