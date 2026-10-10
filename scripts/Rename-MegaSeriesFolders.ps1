[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ApiBaseUrl = "http://127.0.0.1:5080",
    [string]$MegaCmdDir = "",
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-MegaCommandPath {
    param([string]$Name)

    if ($MegaCmdDir) {
        $batchPath = Join-Path $MegaCmdDir "$Name.bat"
        $executablePath = Join-Path $MegaCmdDir "$Name.exe"
        $plainPath = Join-Path $MegaCmdDir $Name

        if (Test-Path $batchPath) { return $batchPath }
        if (Test-Path $executablePath) { return $executablePath }
        if (Test-Path $plainPath) { return $plainPath }

        throw "Commande MEGAcmd introuvable dans '$MegaCmdDir' : $Name"
    }

    $command = Get-Command $Name -ErrorAction Stop
    return $command.Source
}

function ConvertTo-MegaPathSegment {
    param(
        [AllowNull()][string]$Value,
        [Parameter(Mandatory)][string]$Fallback
    )

    $invalidCharacters = [System.IO.Path]::GetInvalidFileNameChars() + [char[]]'/\'
    $sourceValue = if ($null -eq $Value) { "" } else { $Value }
    $characters = foreach ($character in $sourceValue.ToCharArray()) {
        if ($invalidCharacters -contains $character) { ' ' } else { $character }
    }

    $cleaned = (-join $characters) -replace '\s+', ' '
    $cleaned = $cleaned.Trim()
    if ([string]::IsNullOrWhiteSpace($cleaned)) { return $Fallback }
    return $cleaned
}

function Get-CloudSeriesFolder {
    param(
        [Parameter(Mandatory)][string]$CloudPath,
        [Parameter(Mandatory)][string]$Category
    )

    $normalizedPath = $CloudPath.Replace('\', '/').Trim('/')
    $segments = $normalizedPath -split '/'
    $categoryIndex = [Array]::IndexOf($segments, $Category)
    if ($categoryIndex -lt 0 -or $segments.Length -le ($categoryIndex + 1)) {
        return $null
    }

    return "/$($segments[0..($categoryIndex + 1)] -join '/')"
}

function Invoke-MegaFind {
    param([Parameter(Mandatory)][string]$Path)

    $processInfo = New-Object System.Diagnostics.ProcessStartInfo
    $processInfo.FileName = $megaFind
    $processInfo.UseShellExecute = $false
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.StandardOutputEncoding = [System.Text.Encoding]::UTF8
    $processInfo.Arguments = '"' + $Path.Replace('"', '\"') + '"'

    $process = [System.Diagnostics.Process]::Start($processInfo)
    $output = $process.StandardOutput.ReadToEnd()
    $error = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "mega-find a échoué pour '$Path' : $error"
    }

    return @($output -split "`r?`n" | Where-Object { $_ })
}

$megaFind = Get-MegaCommandPath "mega-find"
$megaMove = Get-MegaCommandPath "mega-mv"
$media = Invoke-RestMethod "$($ApiBaseUrl.TrimEnd('/'))/api/media"
$renamePlan = @()
$seriesCategory = "S$([char]0x00E9)ries"
$animeCategory = "Anim$([char]0x00E9)s"
$remoteRoot = "Films - S$([char]0x00E9)ries - Anim$([char]0x00E9)s"
$presentSuffix = "pr$([char]0x00E9)sent"
$manualSeriesMappings = @(
    [PSCustomObject]@{ Pattern = '^24 - Loki - '; TmdbId = 84958; Title = 'Loki'; Year = 2021 }
    [PSCustomObject]@{ Pattern = '^25 - WandaVision - '; TmdbId = 85271; Title = 'WandaVision'; Year = 2021 }
    [PSCustomObject]@{ Pattern = '^26 - The Falcon and the Winter Soldier - '; TmdbId = 88396; Title = "Falcon et le Soldat de l'hiver"; Year = 2021 }
    [PSCustomObject]@{ Pattern = '^31 - Hawkeye - '; TmdbId = 88329; Title = 'Hawkeye'; Year = 2021 }
    [PSCustomObject]@{ Pattern = '^32 - What if'; TmdbId = 91363; Title = 'What If...?'; Year = 2021 }
    [PSCustomObject]@{ Pattern = '^34 - Ms\. Marvel - '; TmdbId = 92782; Title = 'Miss Marvel'; Year = 2022 }
    [PSCustomObject]@{ Pattern = '^37 - Moon Knight - '; TmdbId = 92749; Title = 'Moon Knight'; Year = 2022 }
    [PSCustomObject]@{ Pattern = '^42 - Secret Invasion - '; TmdbId = 114472; Title = 'Secret Invasion'; Year = 2023 }
    [PSCustomObject]@{ Pattern = '^The Wire - 2002-2008$'; TmdbId = 1438; Title = 'Sur écoute'; Year = 2002 }
    [PSCustomObject]@{ Pattern = '^The Sopranos - 1999-2006$'; TmdbId = 1398; Title = 'Les Soprano'; Year = 1999 }
    [PSCustomObject]@{ Pattern = '^The Office \(US\) - 2005-2013$'; TmdbId = 2316; Title = 'The Office'; Year = 2005 }
    [PSCustomObject]@{ Pattern = '^The Man in the High Castle - 2015-2019$'; TmdbId = 62017; Title = 'Le Maître du Haut Château'; Year = 2015 }
    [PSCustomObject]@{ Pattern = "^The Handmaid's Tale - 2017-2025$"; TmdbId = 62127; Title = "The Handmaid's Tale : La Servante écarlate"; Year = 2017 }
    [PSCustomObject]@{ Pattern = '^The End of the Fucking World - 2017-2019$'; TmdbId = 74577; Title = 'The End of the F***ing World'; Year = 2017 }
    [PSCustomObject]@{ Pattern = '^Smiling Friends - 2022-'; TmdbId = 105309; Title = 'Smiling Friends'; Year = 2020 }
    [PSCustomObject]@{ Pattern = '^Rick and Morty - 2013-'; TmdbId = 60625; Title = 'Rick et Morty'; Year = 2013 }
    [PSCustomObject]@{ Pattern = '^Percy Jackson and the Olympians - 2023-'; TmdbId = 94997; Title = 'Percy Jackson et les Olympiens'; Year = 2023 }
    [PSCustomObject]@{ Pattern = '^National Treasure Edge of History - 2023-'; TmdbId = 137437; Title = 'Trésors perdus : Le secret de Moctezuma'; Year = 2022 }
    [PSCustomObject]@{ Pattern = "^Guillermo del Toro's Cabinet of Curiosities - 2022-"; TmdbId = 110316; Title = 'Le Cabinet de curiosités de Guillermo del Toro'; Year = 2022 }
    [PSCustomObject]@{ Pattern = '^Family Guy - 1999-'; TmdbId = 1434; Title = 'Les Griffin'; Year = 1999 }
    [PSCustomObject]@{ Pattern = '^Disenchantment - 2018-'; TmdbId = 73021; Title = 'Désenchantée'; Year = 2018 }
    [PSCustomObject]@{ Pattern = '^Daredevil, Born Again - 2025-'; TmdbId = 202555; Title = 'Daredevil : Born Again'; Year = 2025 }
    [PSCustomObject]@{ Pattern = '^Blood of Zeus - 2022-'; TmdbId = 80986; Title = 'Blood of Zeus'; Year = 2020 }
)

foreach ($category in @($seriesCategory, $animeCategory)) {
    $actualFolders = @(Invoke-MegaFind "/$remoteRoot/$category" |
        ForEach-Object { Get-CloudSeriesFolder -CloudPath $_ -Category $category } |
        Where-Object { $_ } |
        Sort-Object -Unique)
    $actualFolderNames = @($actualFolders | ForEach-Object { Split-Path $_ -Leaf })
    $type = if ($category -eq $animeCategory) { 'Anime' } else { 'Series' }

    foreach ($item in @($media | Where-Object { $_.type -eq $type -and $_.year })) {
        $title = ConvertTo-MegaPathSegment -Value $item.title -Fallback "Média sans titre"
        $targetFolder = "/$remoteRoot/$category/$title - $($item.year)"
        $legacyNamePattern = "^$([regex]::Escape("$title - $($item.year)"))\s*-\s*(?:\d{4}|$presentSuffix)$"
        $sourceFolders = @($actualFolders | Where-Object {
            $name = Split-Path $_ -Leaf
            $name -match $legacyNamePattern
        })

        foreach ($sourceFolder in $sourceFolders) {
            if ($actualFolderNames -contains (Split-Path $targetFolder -Leaf)) {
                Write-Warning "Renommage ignoré car la cible existe déjà : $targetFolder"
                continue
            }
            $renamePlan += [PSCustomObject]@{
                Type = $item.type
                Title = $item.title
                MediaId = $item.id
                Source = $sourceFolder
                Target = $targetFolder
            }
        }
    }
}

$seriesFolders = @(Invoke-MegaFind "/$remoteRoot/$seriesCategory" |
    ForEach-Object { Get-CloudSeriesFolder -CloudPath $_ -Category $seriesCategory } |
    Where-Object { $_ } |
    Sort-Object -Unique)
foreach ($mapping in $manualSeriesMappings) {
    $sourceFolder = $seriesFolders | Where-Object { (Split-Path $_ -Leaf) -match $mapping.Pattern } | Select-Object -First 1
    if (-not $sourceFolder) { continue }

    $targetTitle = ConvertTo-MegaPathSegment -Value $mapping.Title -Fallback "Média sans titre"
    $targetFolder = "/$remoteRoot/$seriesCategory/$targetTitle - $($mapping.Year)"
    if ($seriesFolders -contains $targetFolder) {
        Write-Warning "Renommage ignoré car la cible existe déjà : $targetFolder"
        continue
    }
    $existing = $media | Where-Object { $_.tmdbId -eq $mapping.TmdbId -and $_.type -eq 'Series' } | Select-Object -First 1
    $renamePlan += [PSCustomObject]@{
        Type = 'Series'
        Title = $mapping.Title
        MediaId = if ($existing) { $existing.id } else { $null }
        TmdbId = $mapping.TmdbId
        Source = $sourceFolder
        Target = $targetFolder
    }
}

$renamePlan = @($renamePlan | Group-Object Source | ForEach-Object { $_.Group | Select-Object -First 1 })
if ($renamePlan.Count -eq 0) {
    Write-Host "Aucun dossier de série ou d'animé à renommer."
    exit 0
}

Write-Host "Renommages détectés :"
$renamePlan | Format-Table Type, Title, Source, Target -AutoSize

if (-not $Apply) {
    Write-Host "Simulation uniquement. Relance avec -Apply pour exécuter les renommages."
    exit 0
}

foreach ($rename in $renamePlan) {
    if ($PSCmdlet.ShouldProcess($rename.Source, "Renommer en $($rename.Target)")) {
        if (-not $rename.MediaId) {
            $created = Invoke-RestMethod -Method Post "$($ApiBaseUrl.TrimEnd('/'))/api/media" -ContentType 'application/json' -Body (@{ tmdbId = $rename.TmdbId; type = 'Series' } | ConvertTo-Json)
            $rename.MediaId = $created.id
            Invoke-RestMethod -Method Post "$($ApiBaseUrl.TrimEnd('/'))/api/media/$($rename.MediaId)/episodes/sync" | Out-Null
        }
        & $megaMove $rename.Source $rename.Target
        if ($LASTEXITCODE -ne 0) {
            throw "mega-mv a échoué pour '$($rename.Source)'."
        }
        $relativeFolder = "$seriesCategory/$(Split-Path $rename.Target -Leaf)"
        Invoke-RestMethod -Method Post "$($ApiBaseUrl.TrimEnd('/'))/api/media/$($rename.MediaId)/cloud/sync" -ContentType 'application/json; charset=utf-8' -Body (@{ folder = $relativeFolder } | ConvertTo-Json -Compress) | Out-Null
        Write-Host "Renommé : $($rename.Source) -> $($rename.Target)"
    }
}
