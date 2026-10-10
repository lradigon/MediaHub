param(
    [string]$MovieFile = (Join-Path $PSScriptRoot '../../video.json'),
    [string]$AnimeFile = (Join-Path $PSScriptRoot '../../animation.json'),
    [string]$Destination = (Join-Path $PSScriptRoot '../presets/handbrake.json')
)

$ErrorActionPreference = 'Stop'
$movie = Get-Content $MovieFile -Raw -Encoding UTF8 | ConvertFrom-Json
$anime = Get-Content $AnimeFile -Raw -Encoding UTF8 | ConvertFrom-Json
if (@($movie.PresetList).Count -ne 1 -or @($anime.PresetList).Count -ne 1) {
    throw 'Chaque fichier source doit contenir exactement un preset.'
}
$movie.PresetList[0].PresetName = 'Video'
$anime.PresetList[0].PresetName = 'Animation'
$movie.PresetList = @($movie.PresetList[0], $anime.PresetList[0])
$destinationPath = [IO.Path]::GetFullPath($Destination)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
[IO.File]::WriteAllText($destinationPath, ($movie | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
Write-Host "Presets exportes : $destinationPath"