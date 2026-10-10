param(
    [ValidateSet('up', 'public', 'down', 'restart', 'rebuild', 'status', 'logs', 'test', 'backup', 'config')]
    [string]$Action = 'status',
    [switch]$Public
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$composeArguments = @('compose', '--project-directory', $root, '--profile', 'pipeline')
if ($Public -or $Action -eq 'public' -or $Action -eq 'down') {
    $composeArguments += @('--profile', 'public')
}

function Invoke-Compose([string[]]$Arguments) {
    & docker @composeArguments @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose a echoue (code $LASTEXITCODE)." }
}

if ($Action -in 'up', 'public', 'restart', 'rebuild') {
    if (-not (Test-Path (Join-Path $root '.env'))) {
        throw 'Configure .env a partir de .env.example avant de lancer la stack.'
    }
    if ($Public -or $Action -eq 'public') {
        $rendered = & docker @composeArguments config --format json | Out-String
        if ($LASTEXITCODE -ne 0) { throw 'Configuration Compose invalide.' }
        $configuration = $rendered | ConvertFrom-Json
        $proxy = $configuration.services.caddy.environment
        $password = $configuration.services.db.environment.POSTGRES_PASSWORD
        if ($proxy.MEDIAHUB_DOMAIN -in 'localhost', 'mediahub.example.com' -or
            $proxy.MEDIAHUB_AUTH_PASSWORD_HASH -notmatch '^\$2[aby]\$' -or
            $password -in 'mediahub', 'replace_with_a_long_random_alphanumeric_password' -or $password.Length -lt 20) {
            throw 'Acces public refuse : configure un domaine, un hash bcrypt et un mot de passe PostgreSQL long.'
        }
    }
}

switch ($Action) {
    { $_ -in 'up', 'public', 'rebuild' } { Invoke-Compose @('up', '-d', '--build', '--wait') }
    'restart' { Invoke-Compose @('up', '-d', '--force-recreate', '--wait') }
    'down' { Invoke-Compose @('down') }
    'status' { Invoke-Compose @('ps') }
    'logs' { Invoke-Compose @('logs', '--follow', '--tail', '100', 'api', 'worker', 'web') }
    'config' { Invoke-Compose @('config', '--quiet') }
    'test' {
        & dotnet test (Join-Path $root 'backend/MediaHub.slnx') -c Release
        if ($LASTEXITCODE -ne 0) { throw 'Tests backend en echec.' }
        & npm --prefix (Join-Path $root 'frontend') run build
        if ($LASTEXITCODE -ne 0) { throw 'Build frontend en echec.' }
    }
    'backup' {
        $backupDirectory = Join-Path $root 'backups'
        [IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
        $fileName = 'mediahub-{0}.dump' -f (Get-Date -Format 'yyyyMMdd-HHmmss')
        $temporaryPath = "/tmp/$fileName"
        Invoke-Compose @('exec', '-T', 'db', 'pg_dump', '-U', 'mediahub', '-d', 'mediahub', '-Fc', '-f', $temporaryPath)
        Invoke-Compose @('cp', "db:$temporaryPath", (Join-Path $backupDirectory $fileName))
        Invoke-Compose @('exec', '-T', 'db', 'rm', $temporaryPath)
        Write-Host "Sauvegarde : $(Join-Path $backupDirectory $fileName)"
    }
}