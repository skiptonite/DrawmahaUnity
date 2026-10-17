# Run after az login. Creates billable Azure Storage resources.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z0-9]{3,24}$')]
    [string]$StorageAccountName,
    [string]$ResourceGroupName = 'drawmaha-rg',
    [string]$Location = 'centralus',
    [string]$AppName = 'drawmaha',
    [string]$IdentityPrincipalId,
    [switch]$ConfigureApp
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

$accountKey = $null
try {
    $null = Invoke-Azure -Arguments @('account', 'show', '--output', 'none')
    $null = Invoke-Azure -Arguments @('provider', 'register', '--namespace', 'Microsoft.Storage', '--wait', '--output', 'none')
    $accountNames = Invoke-Azure -Arguments @('storage', 'account', 'list', '--resource-group', $ResourceGroupName, '--query', '[].name', '--output', 'tsv')
    if ($accountNames -notcontains $StorageAccountName) {
        $null = Invoke-Azure -Arguments @('storage', 'account', 'create', '--name', $StorageAccountName, '--resource-group', $ResourceGroupName,
            '--location', $Location, '--sku', 'Standard_LRS', '--kind', 'StorageV2', '--https-only', 'true', '--min-tls-version', 'TLS1_2', '--output', 'none')
    }

    $storageId = (Invoke-Azure -Arguments @('storage', 'account', 'show', '--name', $StorageAccountName, '--resource-group', $ResourceGroupName, '--query', 'id', '--output', 'tsv') | Select-Object -Last 1).ToString().Trim()
    $tableEndpoint = (Invoke-Azure -Arguments @('storage', 'account', 'show', '--name', $StorageAccountName, '--resource-group', $ResourceGroupName, '--query', 'primaryEndpoints.table', '--output', 'tsv') | Select-Object -Last 1).ToString().Trim().TrimEnd('/')
    $accountKey = (Invoke-Azure -Arguments @('storage', 'account', 'keys', 'list', '--account-name', $StorageAccountName, '--resource-group', $ResourceGroupName, '--query', '[0].value', '--output', 'tsv') | Select-Object -Last 1).ToString().Trim()
    if ([string]::IsNullOrWhiteSpace($tableEndpoint) -or [string]::IsNullOrWhiteSpace($accountKey)) { throw 'Could not resolve the storage account table endpoint or provisioning key.' }

    $null = Invoke-Azure -Arguments @('storage', 'table', 'create', '--name', 'playerwallets', '--account-name', $StorageAccountName, '--account-key', $accountKey, '--output', 'none')
    $null = Invoke-Azure -Arguments @('storage', 'table', 'create', '--name', 'handaudits', '--account-name', $StorageAccountName, '--account-key', $accountKey, '--output', 'none')

    if ($ConfigureApp -and [string]::IsNullOrWhiteSpace($IdentityPrincipalId)) {
        $identityName = "$AppName-pull"
        $IdentityPrincipalId = (Invoke-Azure -Arguments @('identity', 'show', '--name', $identityName, '--resource-group', $ResourceGroupName, '--query', 'principalId', '--output', 'tsv') | Select-Object -Last 1).ToString().Trim()
    }
    if (-not [string]::IsNullOrWhiteSpace($IdentityPrincipalId)) {
        $assignments = Invoke-Azure -Arguments @('role', 'assignment', 'list', '--scope', $storageId, '--query', "[?principalId=='$IdentityPrincipalId' && roleDefinitionName=='Storage Table Data Contributor'].id", '--output', 'tsv')
        if (-not $assignments) {
            $null = Invoke-Azure -Arguments @('role', 'assignment', 'create', '--assignee-object-id', $IdentityPrincipalId, '--assignee-principal-type', 'ServicePrincipal', '--role', 'Storage Table Data Contributor', '--scope', $storageId, '--output', 'none')
        }
    }

    if ($ConfigureApp) {
        $identityName = "$AppName-pull"
        $clientId = (Invoke-Azure -Arguments @('identity', 'show', '--name', $identityName, '--resource-group', $ResourceGroupName, '--query', 'clientId', '--output', 'tsv') | Select-Object -Last 1).ToString().Trim()
        $null = Invoke-Azure -Arguments @('containerapp', 'update', '--name', $AppName, '--resource-group', $ResourceGroupName,
            '--set-env-vars', "DRAWMAHA_TABLES_ENDPOINT=$tableEndpoint", "DRAWMAHA_STARTING_BANK=10000", "AZURE_CLIENT_ID=$clientId")
        Write-Host "Configured '$AppName' to use the wallet table. Deploy the updated server image next."
    }
    elseif ([string]::IsNullOrWhiteSpace($IdentityPrincipalId)) {
        Write-Host 'Storage is ready. Grant the Container App identity the Storage Table Data Contributor role before configuring the endpoint.'
    }

    Write-Host "Storage account: $StorageAccountName"
    Write-Host "Table endpoint: $tableEndpoint"
    Write-Output $tableEndpoint
}
finally { $accountKey = $null }
