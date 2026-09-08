[CmdletBinding()]
param([switch] $Restore)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$snapshot = 'artifacts/key-vault-access.json'
if ($Restore) {
    if (!(Test-Path $snapshot)) { return }
    $saved = Get-Content $snapshot -Raw | ConvertFrom-Json
    if ($saved.added) { az keyvault network-rule remove --name $saved.vault --ip-address $saved.ip --output none }
    Write-Output 'Restored Key Vault runner access.'
    return
}
$outputs = Get-Content artifacts/production-outputs.json -Raw | ConvertFrom-Json
$vault = $outputs.deploymentKeyVaultName.value
$ipText = (Invoke-RestMethod https://api.ipify.org).Trim()
$address = $null
if (![Net.IPAddress]::TryParse($ipText, [ref]$address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'Expected the hosted runner IPv4 address.' }
$ip = "$ipText/32"
$rules = @(az keyvault show --name $vault --query 'properties.networkAcls.ipRules[].value' --output json | ConvertFrom-Json)
$added = $ip -notin $rules -and $ipText -notin $rules
@{vault=$vault;ip=$ip;added=$added} | ConvertTo-Json | Set-Content $snapshot -Encoding utf8NoBOM
if ($added) { az keyvault network-rule add --name $vault --ip-address $ip --output none }
