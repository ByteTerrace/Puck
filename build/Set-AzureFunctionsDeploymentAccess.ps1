[CmdletBinding()]
param([switch] $Restore, [string] $ResourceGroup = 'byteterrace', [string] $Name = 'bytrcfuncp000')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$snapshot = 'artifacts/functions-scm-access.json'
if ($Restore) {
    if (!(Test-Path $snapshot)) { return }
    $saved = Get-Content $snapshot -Raw | ConvertFrom-Json
    az rest --method put --url $saved.url --body "@$($saved.bodyFile)" --output none
    Write-Output 'Restored the original Functions deployment-site restrictions.'
    return
}
$id = (az functionapp show --name $Name --resource-group $ResourceGroup --query id --output tsv).Trim()
$url = "https://management.azure.com$id/config/web?api-version=2024-04-01"
$config = az rest --method get --url $url --output json | ConvertFrom-Json
$properties = @{
    scmIpSecurityRestrictions = $config.properties.scmIpSecurityRestrictions
    scmIpSecurityRestrictionsDefaultAction = $config.properties.scmIpSecurityRestrictionsDefaultAction
    scmIpSecurityRestrictionsUseMain = $config.properties.scmIpSecurityRestrictionsUseMain
}
New-Item artifacts -ItemType Directory -Force | Out-Null
@{properties=$properties} | ConvertTo-Json -Depth 20 | Set-Content artifacts/functions-scm-restore.json -Encoding utf8NoBOM
@{url=$url;bodyFile='artifacts/functions-scm-restore.json'} | ConvertTo-Json | Set-Content $snapshot -Encoding utf8NoBOM
$ipText = (Invoke-RestMethod https://api.ipify.org).Trim()
$ip = $null
if (![Net.IPAddress]::TryParse($ipText, [ref]$ip) -or $ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'Expected the hosted runner IPv4 address.' }
# Only the authenticated deployment endpoint admits this runner. Application ingress is unchanged.
az webapp config access-restriction add --resource-group $ResourceGroup --name $Name --scm-site true --rule-name "gha-$env:GITHUB_RUN_ID" --action Allow --ip-address "$ipText/32" --priority 100 --output none
az webapp config access-restriction set --resource-group $ResourceGroup --name $Name --use-same-restrictions-for-scm-site false --scm-default-action Deny --output none
