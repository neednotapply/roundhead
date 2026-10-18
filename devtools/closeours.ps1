# Closes only the Cuphead/ROUNDS/launcher processes this dev setup started: their command line points at the dev work
# folder (CUPROUNDS_WORK, default ~/cuprounds-work). Games you started yourself are left alone.
param([string]$Marker = "")
if (-not $Marker) {
    if ($env:CUPROUNDS_WORK) { $Marker = Split-Path -Leaf $env:CUPROUNDS_WORK } else { $Marker = "cuprounds-work" }
}
$ours = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and $_.CommandLine -like "*$Marker*" -and ($_.Name -in @("Cuphead.exe", "Rounds.exe", "CupRoundsLauncher.exe")) }
foreach ($p in ($ours | Sort-Object { if ($_.Name -eq "Cuphead.exe") { 0 } else { 1 } })) {
    $proc = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
    if ($proc) {
        $proc.CloseMainWindow() | Out-Null
        if (-not $proc.WaitForExit(8000)) { Stop-Process -Id $p.ProcessId -ErrorAction SilentlyContinue }
        "closed $($p.Name) $($p.ProcessId)"
    }
}
