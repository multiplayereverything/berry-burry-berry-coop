# Starts a self-driving host and guest copy of the game on this PC and waits for both to finish.
# Usage: powershell -File tools\lab.ps1 [-Online] [-Relay 127.0.0.1:7799] [-Minutes 6]
param([switch]$Online, [string]$Relay = "", [int]$Minutes = 6, [switch]$HostLeaves)

$game = "F:\SteamLibrary\steamapps\common\BerryBerryBerry"
$exe = Join-Path $game "BerryBerryBerryBerryBerryBerryBerryBerryBerry.exe"
$lab = Join-Path $PSScriptRoot "..\lab"
New-Item -ItemType Directory -Force $lab | Out-Null
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $lab "join.txt"), (Join-Path $lab "*.png")

$lab = (Resolve-Path $lab).Path
$common = @("-screen-fullscreen", "0", "-screen-width", "800", "-screen-height", "450", "-berrycoop-labdir", ('"' + $lab + '"'))
if ($Relay -ne "") { $common += @("-berrycoop-relay", $Relay) }
$hostArgs = $common + @("-berrycoop-lab", "host", "-berrycoop-name", "Hosty")
if ($Online) { $hostArgs += "-berrycoop-online" }
$guestArgs = $common + @("-berrycoop-lab", "guest", "-berrycoop-name", "Guesty")

$h = Start-Process -FilePath $exe -ArgumentList $hostArgs -WorkingDirectory $game -PassThru
Start-Sleep -Seconds 8
$g = Start-Process -FilePath $exe -ArgumentList $guestArgs -WorkingDirectory $game -PassThru
"host pid $($h.Id), guest pid $($g.Id)"

$deadline = (Get-Date).AddMinutes($Minutes)
$cfg = Join-Path $game "BepInEx\config\BerryCoop"
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 5
    $hl = Join-Path $cfg "lab-$($h.Id).log"; $gl = Join-Path $cfg "lab-$($g.Id).log"
    $hostDone = (Test-Path $hl) -and (Select-String -Path $hl -Pattern "LAB DONE host" -Quiet)
    $guestDone = (Test-Path $gl) -and (Select-String -Path $gl -Pattern "LAB DONE guest" -Quiet)
    if ($hostDone -and $guestDone) { break }
    if ($h.HasExited -or $g.HasExited) { "a game exited early"; break }
}
Start-Sleep -Seconds 2
if ($HostLeaves -and -not $h.HasExited) {
    Stop-Process -Id $h.Id -Force
    "host closed; watching the guest for 10 s"
    Start-Sleep -Seconds 10
}
foreach ($p in @($h, $g)) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
"logs: lab-$($h.Id).log lab-$($g.Id).log"
