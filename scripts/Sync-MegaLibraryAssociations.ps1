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
$cloudRoot = "/$remoteRoot"
$categories = @(
    [PSCustomObject]@{ Folder = "S${accentE}ries"; Type = 'Series' },
    [PSCustomObject]@{ Folder = "Anim${accentE}s"; Type = 'Anime' }
)
$media = @((Invoke-RestMethod "$($ApiBaseUrl.TrimEnd('/'))/api/media") | ForEach-Object { $_ })

# A duplicate TMDB record exists for Slime; only this record owns the cloud files.
$manualMediaMappings = @{
    "Anim${accentE}s/Moi, quand je me r${accentE}incarne en Slime - 2018" = '5083805c-ca29-4e0b-b8db-5465604cc258'
}

$plan = @()
$skipped = @()
foreach ($category in $categories) {
    $categoryPath = "$cloudRoot/$($category.Folder)"
    $folders = @((Invoke-MegaCommand -Command 'mega-find' -Arguments @($categoryPath)) -split "`r?`n" |
        Where-Object { $_ -match "^$([regex]::Escape($categoryPath))/[^/]+$" } |
        ForEach-Object { $_.Substring($categoryPath.Length + 1) } |
        Sort-Object -Unique)

    foreach ($folder in $folders) {
        $relativeFolder = "$($category.Folder)/$folder"
        $matches = @()
        if ($manualMediaMappings.ContainsKey($relativeFolder)) {
            $matches = @($media | Where-Object { $_.id -eq $manualMediaMappings[$relativeFolder] })
        }
        elseif ($folder -match '^(?<title>.+) - (?<year>\d{4})$') {
            $title = $Matches['title']
            $year = [int]$Matches['year']
            $matches = @($media | Where-Object {
                $_.type -eq $category.Type -and $_.year -eq $year -and
                (Get-SafeFolderName -Title ([string]$_.title)) -eq $title
            })
        }

        if ($matches.Count -ne 1) {
            $skipped += [PSCustomObject]@{
                Category = $category.Folder
                Folder = $folder
                Reason = if ($matches.Count -eq 0) { 'Aucune fiche exacte' } else { 'Plusieurs fiches correspondantes' }
            }
            continue
        }

        $item = $matches[0]
        $plan += [PSCustomObject]@{
            MediaId = $item.id
            Title = $item.title
            Type = $category.Type
            Folder = $relativeFolder
        }
    }
}

Write-Host "Associations exactes prêtes : $($plan.Count)"
Write-Host "Dossiers ignorés : $($skipped.Count)"
if ($skipped.Count -gt 0) {
    $skipped | Format-Table Category, Folder, Reason -AutoSize
}

if (-not $Apply) {
    Write-Host 'Simulation uniquement. Relance avec -Apply pour synchroniser les associations exactes.'
    exit 0
}

$results = @()
foreach ($association in $plan) {
    if (-not $PSCmdlet.ShouldProcess($association.Folder, "Rattacher à $($association.Title)")) {
        continue
    }

    $detail = Invoke-RestMethod "$($ApiBaseUrl.TrimEnd('/'))/api/media/$($association.MediaId)"
    if ($association.Type -ne 'Movie' -and @($detail.seasons).Count -eq 0) {
        Invoke-RestMethod -Method Post "$($ApiBaseUrl.TrimEnd('/'))/api/media/$($association.MediaId)/episodes/sync" | Out-Null
    }

    $summary = Invoke-RestMethod -Method Post "$($ApiBaseUrl.TrimEnd('/'))/api/media/$($association.MediaId)/cloud/sync" `
        -ContentType 'application/json; charset=utf-8' `
        -Body (@{ folder = $association.Folder } | ConvertTo-Json -Compress)
    $results += [PSCustomObject]@{
        Title = $association.Title
        Folder = $association.Folder
        FilesFound = $summary.filesFound
        EpisodesLinked = $summary.episodesLinked
    }
}

Write-Host "Associations synchronisées : $($results.Count)"
$results | Format-Table Title, FilesFound, EpisodesLinked, Folder -AutoSize