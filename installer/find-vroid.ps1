# Shared by install.ps1 and uninstall.ps1: finds the Steam copy of VRoid Studio.
$SteamAppId = '1486350'
$ExeName    = 'VRoidStudio.exe'

function Find-VRoid {
    $candidates = New-Object System.Collections.Generic.List[string]
    $steam = $null
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        $p = Get-ItemProperty $key -ErrorAction SilentlyContinue
        if ($p.SteamPath) { $steam = $p.SteamPath; break }
        if ($p.InstallPath) { $steam = $p.InstallPath; break }
    }
    if ($steam) {
        $steam = $steam -replace '/', '\'
        $libs = New-Object System.Collections.Generic.List[string]
        $libs.Add($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in (Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"' -AllMatches)) {
                foreach ($mm in $m.Matches) { $libs.Add(($mm.Groups[1].Value -replace '\\\\', '\')) }
            }
        }
        foreach ($lib in ($libs | Select-Object -Unique)) {
            $acf = Join-Path $lib "steamapps\appmanifest_$SteamAppId.acf"
            if (Test-Path $acf) {
                $m = Select-String -Path $acf -Pattern '"installdir"\s+"([^"]+)"' | Select-Object -First 1
                if ($m) { $candidates.Add((Join-Path $lib ("steamapps\common\" + $m.Matches[0].Groups[1].Value))) }
            }
            $candidates.Add((Join-Path $lib 'steamapps\common\VRoid Studio'))
            $candidates.Add((Join-Path $lib 'steamapps\common\VRoidStudio'))
        }
    }
    foreach ($c in $candidates) { if (Test-Path (Join-Path $c $ExeName)) { return $c } }
    return $null
}
