[CmdletBinding()]
param(
    [switch]$LocalServer
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$executable = Join-Path $projectRoot 'Builds/Windows/Drawmaha.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'Build the Windows client in Unity first: Tools > Drawmaha > Build Windows Client.'
}
foreach ($player in 1, 2) {
    $arguments = @('-drawmaha-test-player', $player, '-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '540')
    if ($LocalServer) { $arguments += @('-drawmaha-server', 'ws://127.0.0.1:8080/ws') }
    # Visible interactive windows are the requested result of this launcher.
    Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable -Parent) -ArgumentList $arguments -WindowStyle Normal
}
Write-Host 'Opened Test Player 1 and Test Player 2. Choose a display name on first launch; saved players connect automatically. Join the same table.'
if ($LocalServer) { Write-Host 'These windows expect a local game server already running at 127.0.0.1:8080.' }
