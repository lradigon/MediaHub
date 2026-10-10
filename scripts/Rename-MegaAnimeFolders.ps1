[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ApiBaseUrl = "http://127.0.0.1:5080",
    [string]$MegaCmdDir = "C:\Users\Radigon\AppData\Local\MEGAcmd",
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-MegaCommand {
    param(
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $processInfo = New-Object System.Diagnostics.ProcessStartInfo
    $processInfo.FileName = Join-Path $MegaCmdDir "$Command.bat"
    $processInfo.Arguments = ($Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $processInfo.UseShellExecute = $false
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.StandardOutputEncoding = [Text.Encoding]::UTF8

    $process = [System.Diagnostics.Process]::Start($processInfo)
    $output = $process.StandardOutput.ReadToEnd()
    $error = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    if ($process.ExitCode -ne 0 -and [string]::IsNullOrWhiteSpace($output)) {
        throw "$Command a échoué : $error"
    }

    return $output
}

function Get-SafeFolderName {
    param([Parameter(Mandatory)][string]$Title)

    return (($Title -replace '[\\/:*?"<>|]', ' ') -replace '\s+', ' ').Trim()
}

$accentE = [char]0x00E9
$remoteRoot = "Films - S${accentE}ries - Anim${accentE}s"
$category = "Anim${accentE}s"
$cloudRoot = "/$remoteRoot/$category"
$presentSuffix = "pr${accentE}sent"
$media = @((Invoke-RestMethod "$($ApiBaseUrl.TrimEnd('/'))/api/media") | Where-Object { $_.type -eq 'Anime' })
$folders = @((Invoke-MegaCommand -Command 'mega-find' -Arguments @($cloudRoot)) -split "`r?`n" |
    Where-Object { $_ -match "^$([regex]::Escape($cloudRoot))/[^/]+$" } |
    ForEach-Object { $_.Substring($cloudRoot.Length + 1) } |
    Sort-Object -Unique)

# These names are aliases or legacy formats that cannot be matched from the folder name alone.
$manualMappings = @{
    'Akame Ga Kill! - 2014' = 61223
    'Avatar The Last Airbender - 2005-2008' = 246
    "Boruto - 2017-$presentSuffix" = 70881
    'Castlevania Nocturne - 2023' = 123548
    'Code Geass - 2006-2008' = 31724
    'Cyberpunk Edgerunners - 2022' = 105248
    'Daemons of the Shadow Realm - 2026' = 260463
    "Dandadan - 2024-$presentSuffix" = 240411
    "Demon Slayer - 2019-$presentSuffix" = 85937
    'Evangelion - 1995-1996' = 890
    'Fullmetal Alchemist - 2003-2004' = 37863
    'Fullmetal Alchemist Brotherhood - 2009' = 31911
    "Gambling School - 2018-$presentSuffix" = 72305
    "Haikyuu - 2014-$presentSuffix" = 60863
    'Hellsing The Dawn - 2011' = 115176
    "Mashle - 2023-$presentSuffix" = 204832
    "Mushoku Tensei - 2021-$presentSuffix" = 94664
    'Parasyte - 2014-2015' = 61459
    'Pokémon Origins - 2013' = 61295
    "Re Zero - 2016-$presentSuffix" = 65942
    "Solo Leveling - 2023-$presentSuffix" = 127532
    "Shingeki no Kyojin - 2013-$presentSuffix" = 1429
    "That Time I Got Reincarnated as a Slime - 2018-$presentSuffix" = 82684
    "Tatsuki Fujimoto 17-26 - 2025-$presentSuffix" = 299778
    'Tonari no Seki-kun - 2014' = 70036
    'Uzumaki - 2024' = 93241
    'Yu Yu Hakusho - 1992-1994' = 30669
    'Zom 100 Bucket List of the Dead - 2023' = 217766
}
$manualMediaMappings = @{
    "That Time I Got Reincarnated as a Slime - 2018-$presentSuffix" = '5083805c-ca29-4e0b-b8db-5465604cc258'
}

$plan = foreach ($sourceName in $folders) {
    $matches = @()
    if ($manualMediaMappings.ContainsKey($sourceName)) {
        $matches = @($media | Where-Object { $_.id -eq $manualMediaMappings[$sourceName] })
    }
    elseif ($manualMappings.ContainsKey($sourceName)) {
        $matches = @($media | Where-Object { $_.tmdbId -eq $manualMappings[$sourceName] })
    }
    elseif ($sourceName -match "^(?<title>.+) - (?<year>\d{4})(?:-(?:\d{4}|$([regex]::Escape($presentSuffix))))?$") {
        $legacyTitle = $Matches['title']
        $legacyYear = [int]$Matches['year']
        $matches = @($media | Where-Object {
            $_.year -eq $legacyYear -and (Get-SafeFolderName -Title ([string]$_.title)) -eq $legacyTitle
        })
    }

    if ($matches.Count -ne 1) { continue }

    $item = $matches[0]
    $targetName = "$(Get-SafeFolderName -Title ([string]$item.title)) - $($item.year)"
    if ($sourceName -eq $targetName) { continue }

    [PSCustomObject]@{
        MediaId = $item.id
        Title = $item.title
        Source = $sourceName
        Target = $targetName
    }
}

$plan = @($plan)
if ($plan.Count -eq 0) {
    Write-Host 'Aucun dossier d’animé à renommer.'
    exit 0
}

Write-Host 'Renommages d’animés détectés :'
$plan | Format-Table Title, Source, Target -AutoSize

if (-not $Apply) {
    Write-Host 'Simulation uniquement. Relance avec -Apply pour exécuter.'
    exit 0
}

foreach ($rename in $plan) {
    $sourcePath = "$cloudRoot/$($rename.Source)"
    $targetPath = "$cloudRoot/$($rename.Target)"
    if ($folders -contains $rename.Target) {
        Write-Warning "Cible déjà présente, ignorée : $targetPath"
        continue
    }

    if ($PSCmdlet.ShouldProcess($sourcePath, "Renommer en $targetPath")) {
        Invoke-MegaCommand -Command 'mega-mv' -Arguments @($sourcePath, $targetPath) | Out-Null
        $body = @{ folder = "$category/$($rename.Target)" } | ConvertTo-Json -Compress
        Invoke-RestMethod -Method Post "$($ApiBaseUrl.TrimEnd('/'))/api/media/$($rename.MediaId)/cloud/sync" `
            -ContentType 'application/json; charset=utf-8' -Body $body | Out-Null
        Write-Host "Renommé et synchronisé : $($rename.Source) -> $($rename.Target)"
    }
}