# Run from PowerShell after az login. Creates billable Azure resources; does not run automatically.
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z0-9]{5,50}$')]
    [string]$RegistryName,
    [ValidatePattern('^[a-z0-9]{3,24}$')]
    [string]$StorageAccountName,
    [string]$ResourceGroupName = 'drawmaha-rg',
    [string]$Location = 'centralus',
    [string]$AppName = 'drawmaha'
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
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    Invoke-Azure -Arguments @('extension', 'add', '--name', 'containerapp', '--upgrade', '--output', 'none')
    Invoke-Azure -Arguments @('provider', 'register', '--namespace', 'Microsoft.App', '--wait', '--output', 'none')
    Invoke-Azure -Arguments @('provider', 'register', '--namespace', 'Microsoft.ContainerRegistry', '--wait', '--output', 'none')
    Invoke-Azure -Arguments @('provider', 'register', '--namespace', 'Microsoft.ManagedIdentity', '--wait', '--output', 'none')
    Invoke-Azure -Arguments @('provider', 'register', '--namespace', 'Microsoft.Storage', '--wait', '--output', 'none')
    Invoke-Azure -Arguments @('group', 'create', '--name', $ResourceGroupName, '--location', $Location, '--output', 'none')
    # Refuse to redeploy an existing game; it may have live, in-memory tables.
    $existingAppNames = Invoke-Azure -Arguments @('containerapp', 'list', '--resource-group', $ResourceGroupName, '--query', '[].name', '--output', 'tsv')
    if ($existingAppNames -contains $AppName) { throw 'This app already exists. Deploy updates only during an empty-table maintenance window; see AZURE_SETUP.md.' }
    $registryNames = Invoke-Azure -Arguments @('acr', 'list', '--resource-group', $ResourceGroupName, '--query', '[].name', '--output', 'tsv')
    if ($registryNames -notcontains $RegistryName) {
        Invoke-Azure -Arguments @('acr', 'create', '--name', $RegistryName, '--resource-group', $ResourceGroupName, '--sku', 'Basic', '--output', 'none')
    }
    $registryId = Invoke-Azure -Arguments @('acr', 'show', '-n', $RegistryName, '-g', $ResourceGroupName, '--query', 'id', '-o', 'tsv')
    $registryServer = Invoke-Azure -Arguments @('acr', 'show', '-n', $RegistryName, '-g', $ResourceGroupName, '--query', 'loginServer', '-o', 'tsv')
    $identityName = "$AppName-pull"
    $identityNames = Invoke-Azure -Arguments @('identity', 'list', '--resource-group', $ResourceGroupName, '--query', '[].name', '--output', 'tsv')
    if ($identityNames -notcontains $identityName) {
        Invoke-Azure -Arguments @('identity', 'create', '--name', $identityName, '--resource-group', $ResourceGroupName, '--output', 'none')
    }
    $identityId = Invoke-Azure -Arguments @('identity', 'show', '-n', $identityName, '-g', $ResourceGroupName, '--query', 'id', '-o', 'tsv')
    $principalId = Invoke-Azure -Arguments @('identity', 'show', '-n', $identityName, '-g', $ResourceGroupName, '--query', 'principalId', '-o', 'tsv')
    $identityClientId = Invoke-Azure -Arguments @('identity', 'show', '-n', $identityName, '-g', $ResourceGroupName, '--query', 'clientId', '-o', 'tsv')
    $pullAssignments = Invoke-Azure -Arguments @('role', 'assignment', 'list', '--scope', $registryId, '--query', "[?principalId=='$principalId' && roleDefinitionName=='AcrPull'].id", '--output', 'tsv')
    if (-not $pullAssignments) {
        Invoke-Azure -Arguments @('role', 'assignment', 'create', '--assignee-object-id', $principalId, '--assignee-principal-type', 'ServicePrincipal', '--role', 'AcrPull', '--scope', $registryId, '--output', 'none')
    }
    if ([string]::IsNullOrWhiteSpace($StorageAccountName)) { $StorageAccountName = 'drawmaha' + [Guid]::NewGuid().ToString('N').Substring(0, 8) }
    $walletEndpointOutput = & (Join-Path $PSScriptRoot 'setup-wallet-storage.ps1') -StorageAccountName $StorageAccountName -ResourceGroupName $ResourceGroupName -Location $Location -IdentityPrincipalId $principalId
    $walletEndpoint = ($walletEndpointOutput | Select-Object -Last 1).ToString().Trim()
    if ([string]::IsNullOrWhiteSpace($walletEndpoint)) { throw 'Wallet storage setup did not return a Table endpoint.' }
    $tag = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss')
    $buildContext = Join-Path ([IO.Path]::GetTempPath()) "drawmaha-acr-build-$tag"
    try {
        $null = New-Item -ItemType Directory -Path (Join-Path $buildContext 'Server') -Force
        $null = New-Item -ItemType Directory -Path (Join-Path $buildContext 'Assets/Drawmaha') -Force
        $null = New-Item -ItemType Directory -Path (Join-Path $buildContext 'Assets/Drawmaha/Network') -Force
        Copy-Item -Path 'Server/Drawmaha.Server.csproj', 'Server/appsettings.json', 'Server/Dockerfile' -Destination (Join-Path $buildContext 'Server')
        Copy-Item -Path 'Server/*.cs' -Destination (Join-Path $buildContext 'Server')
        Copy-Item -Path 'Assets/Drawmaha/Core' -Destination (Join-Path $buildContext 'Assets/Drawmaha') -Recurse
        Copy-Item -Path 'Assets/Drawmaha/Network/Wire.cs' -Destination (Join-Path $buildContext 'Assets/Drawmaha/Network')
        Invoke-Azure -Arguments @('acr', 'build', '--registry', $RegistryName, '--image', "drawmaha:$tag", '--file', 'Server/Dockerfile', $buildContext)
    }
    finally { Remove-Item -LiteralPath $buildContext -Recurse -Force -ErrorAction SilentlyContinue }
    $environmentName = "$AppName-env"
    $auditKeyBytes = New-Object byte[] 32
    $auditGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $auditGenerator.GetBytes($auditKeyBytes); $plainAuditKey = [Convert]::ToBase64String($auditKeyBytes) }
    finally { $auditGenerator.Dispose(); [Array]::Clear($auditKeyBytes, 0, $auditKeyBytes.Length) }
    Invoke-Azure -Arguments @('containerapp', 'env', 'create', '--name', $environmentName, '--resource-group', $ResourceGroupName, '--location', $Location, '--logs-destination', 'none', '--output', 'none')
    Invoke-Azure -Arguments @('containerapp', 'create', '--name', $AppName, '--resource-group', $ResourceGroupName,
        '--environment', $environmentName, '--image', "$registryServer/drawmaha:$tag",
        '--user-assigned', $identityId, '--registry-identity', $identityId, '--registry-server', $registryServer,
        '--ingress', 'external', '--target-port', '8080', '--transport', 'http',
        '--cpu', '0.25', '--memory', '0.5Gi', '--min-replicas', '1', '--max-replicas', '1', '--revisions-mode', 'single',
        '--secrets', "audit-key=$plainAuditKey", '--env-vars', 'DRAWMAHA_AUDIT_KEY=secretref:audit-key', 'ASPNETCORE_ENVIRONMENT=Production',
        "DRAWMAHA_TABLES_ENDPOINT=$walletEndpoint", 'DRAWMAHA_STARTING_BANK=10000', "AZURE_CLIENT_ID=$identityClientId", '--output', 'none')
    $domain = Invoke-Azure -Arguments @('containerapp', 'show', '--name', $AppName, '--resource-group', $ResourceGroupName, '--query', 'properties.configuration.ingress.fqdn', '-o', 'tsv')
    Write-Host "Unity server address: wss://$domain/ws"
    Write-Host "Health check: https://$domain/health"
    Write-Host "Wallet storage account: $StorageAccountName"
    Write-Host 'Set an Azure Cost Management budget for this resource group.'
}
finally { $plainAuditKey = $null; Pop-Location }
