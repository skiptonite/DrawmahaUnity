[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z0-9]{5,50}$')]
    [string]$RegistryName,
    [string]$ResourceGroupName = 'drawmaha-rg',
    [string]$AppName = 'drawmaha',
    [ValidateRange(1, 30)]
    [int]$HealthCheckAttempts = 12,
    [ValidateRange(1, 30)]
    [int]$HealthCheckIntervalSeconds = 5
)

$ErrorActionPreference = 'Stop'

function Invoke-Azure {
    param([string[]]$Arguments)
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $result = & az @Arguments --only-show-errors 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousErrorActionPreference }
    if ($exitCode -ne 0) {
        $details = ($result | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        throw "Azure CLI command failed: $($Arguments[0]) $($Arguments[1])`n$details"
    }
    return $result
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) { throw 'Azure CLI (az) is required. Install it and sign in with az login.' }

$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $null = Invoke-Azure -Arguments @('account', 'show', '--output', 'none')
    $registryOutput = Invoke-Azure -Arguments @('acr', 'show', '--name', $RegistryName, '--resource-group', $ResourceGroupName, '--query', 'loginServer', '--output', 'tsv')
    $registryServer = ($registryOutput | Select-Object -Last 1).ToString().Trim()
    if ([string]::IsNullOrWhiteSpace($registryServer)) { throw "Could not resolve the login server for registry '$RegistryName'." }

    $appOutput = Invoke-Azure -Arguments @('containerapp', 'show', '--name', $AppName, '--resource-group', $ResourceGroupName, '--query', 'properties.configuration.ingress.fqdn', '--output', 'tsv')
    $fqdn = ($appOutput | Select-Object -Last 1).ToString().Trim()
    if ([string]::IsNullOrWhiteSpace($fqdn)) { throw "Container App '$AppName' has no external ingress hostname." }

    $tag = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
    $buildContext = Join-Path ([IO.Path]::GetTempPath()) "drawmaha-acr-build-$tag"
    try {
        $null = New-Item -ItemType Directory -Path (Join-Path $buildContext 'Server') -Force
        $null = New-Item -ItemType Directory -Path (Join-Path $buildContext 'Assets/Drawmaha') -Force
        $null = New-Item -ItemType Directory -Path (Join-Path $buildContext 'Assets/Drawmaha/Network') -Force
        Copy-Item -Path 'Server/Drawmaha.Server.csproj', 'Server/appsettings.json', 'Server/Dockerfile' -Destination (Join-Path $buildContext 'Server')
        Copy-Item -Path 'Server/*.cs' -Destination (Join-Path $buildContext 'Server')
        Copy-Item -Path 'Assets/Drawmaha/Core' -Destination (Join-Path $buildContext 'Assets/Drawmaha') -Recurse
        Copy-Item -Path 'Assets/Drawmaha/Network/Wire.cs' -Destination (Join-Path $buildContext 'Assets/Drawmaha/Network')
        Write-Host "Building $registryServer/drawmaha:$tag"
        $null = Invoke-Azure -Arguments @('acr', 'build', '--registry', $RegistryName, '--image', "drawmaha:$tag", '--file', 'Server/Dockerfile', $buildContext)
    }
    finally { Remove-Item -LiteralPath $buildContext -Recurse -Force -ErrorAction SilentlyContinue }

    Write-Host ''
    Write-Host "This update restarts '$AppName' and clears in-memory tables and reconnect sessions."
    Write-Host 'The last completed-hand table stacks are checkpointed to wallets; any unfinished hand is rolled back.'
    Write-Host 'Make sure every table is empty before continuing.'
    if ((Read-Host 'Type EMPTY to deploy') -cne 'EMPTY') { throw 'Deployment cancelled. The running app was not updated.' }

    # Preserve the audit key across revisions so historical hands remain decryptable.
    $secretNames = Invoke-Azure -Arguments @('containerapp', 'show', '--name', $AppName, '--resource-group', $ResourceGroupName,
        '--query', 'properties.configuration.secrets[].name', '--output', 'tsv')
    if ($secretNames -notcontains 'audit-key') {
        $auditKeyBytes = New-Object byte[] 32
        $auditGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try {
            $auditGenerator.GetBytes($auditKeyBytes)
            $plainAuditKey = [Convert]::ToBase64String($auditKeyBytes)
            $null = Invoke-Azure -Arguments @('containerapp', 'secret', 'set', '--name', $AppName, '--resource-group', $ResourceGroupName,
                '--secrets', "audit-key=$plainAuditKey", '--output', 'none')
        }
        finally { $auditGenerator.Dispose(); [Array]::Clear($auditKeyBytes, 0, $auditKeyBytes.Length); $plainAuditKey = $null }
    }

    $image = "$registryServer/drawmaha:$tag"
    Write-Host "Updating '$AppName' to $image"
    $null = Invoke-Azure -Arguments @('containerapp', 'update', '--name', $AppName, '--resource-group', $ResourceGroupName,
        '--image', $image, '--min-replicas', '1', '--max-replicas', '1', '--set-env-vars', 'DRAWMAHA_AUDIT_KEY=secretref:audit-key', '--remove-env-vars', 'DRAWMAHA_ACCESS_KEY')

    $healthUrl = "https://$fqdn/health"
    $healthy = $false
    $lastHealthError = 'Health endpoint did not return the expected status.'
    for ($attempt = 1; $attempt -le $HealthCheckAttempts; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri $healthUrl -Method Get -TimeoutSec 10
            if ($health.status -eq 'ok' -and $health.protocol -eq 11) { $healthy = $true; break }
            $lastHealthError = "Unexpected health response: $($health | ConvertTo-Json -Compress)"
        }
        catch { $lastHealthError = $_.Exception.Message }
        if ($attempt -lt $HealthCheckAttempts) { Start-Sleep -Seconds $HealthCheckIntervalSeconds }
    }
    if (-not $healthy) { throw "Image was updated, but health check failed at $healthUrl. $lastHealthError" }

    Write-Host "Deployment healthy: $healthUrl"
    Write-Host "Image: $image"
}
finally { Pop-Location }
